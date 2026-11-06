using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Pdf.Native;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.Optimize;

/// <summary>한 이미지를 줄인 결과(JBIG2 스트림과 새 화소 크기).</summary>
public sealed record BilevelResult(byte[] Jbig2, int Width, int Height);

/// <summary>같은 이미지·같은 축소 배율의 결과를 단계표의 여러 단계에서 다시 만들지 않으려는 캐시(JBIG2 압축은 오래 걸린다).</summary>
public sealed class BilevelCache
{
    public ConcurrentDictionary<(int Obj, int Factor), BilevelResult?> Results { get; } = new();
}

/// <summary>
/// 1비트 흑백 스캔 이미지(CCITT·Flate·무압축, DeviceGray)를 절반 이하로 줄여 JBIG2로 다시 압축한다.
/// 원본 스트림을 이미지 한 장짜리 임시 PDF로 만들어 PDFium으로 그리면 어떤 필터든 같은 방식으로 풀리므로 디코더를 따로 두지 않는다.
/// 줄일 때는 블록 안의 검은 화소가 일정 비율 이상이면 검정으로 해서 가는 선이 사라지지 않게 한다.
/// </summary>
internal static class BilevelRecompressor
{
    /// <summary>1비트는 이 dpi 밑으로는 줄이지 않는다(2값 그림은 낮추면 선이 끊겨 읽기 어렵다).</summary>
    public const int FloorDpi = 200;

    public static bool IsCandidate(PdfDictionary d)
    {
        var e = d.Elements;
        if (d.Stream is null || e.GetInteger("/BitsPerComponent") != 1) return false;
        if (Flag(e, "/ImageMask") || e.ContainsKey("/Mask") || e.ContainsKey("/SMask") || e.ContainsKey("/Decode")) return false;
        var cs = Resolve(e, "/ColorSpace");
        if (cs is not null && cs is not PdfName { Value: "/DeviceGray" }) return false;
        var f = Resolve(e, "/Filter");
        string? filter = f switch
        {
            null => "",
            PdfName n => n.Value,
            PdfArray { Elements.Count: 1 } a when a.Elements[0] is PdfName n1 => n1.Value,
            _ => null,
        };
        return filter is "" or "/CCITTFaxDecode" or "/FlateDecode";
    }

    /// <summary>단계의 dpi와 이미지의 유효 dpi에서 정수 축소 배율(1 이상). 배치를 모르면(0) 1.</summary>
    public static int Factor(double imageDpi, int levelMaxDpi)
    {
        int target = Math.Max(levelMaxDpi, FloorDpi);
        return imageDpi <= 0 ? 1 : Math.Max(1, (int)Math.Floor(imageDpi / target));
    }

    /// <summary>호출 스레드는 PDFium 스레드가 아니어야 한다. PDFsharp 객체는 건드리지 않으므로 병렬로 불러도 된다.</summary>
    public static BilevelResult? Compute(PdfiumThread t, byte[] tempPdf, int width, int height, int factor, SemaphoreSlim renderGate, CancellationToken ct)
    {
        byte[]? bgra;
        renderGate.Wait(ct);
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "sedol-bilevel-" + Guid.NewGuid().ToString("N") + ".pdf");
            File.WriteAllBytes(path, tempPdf);
            try
            {
                bgra = t.InvokeAsync(() =>
                {
                    using var doc = Pdf.Native.PdfDocument.Open(t, path, null);
                    using var page = doc.LoadPage(0);
                    return page.RenderTile(1.0, 0, 0, width, height, false);
                }, ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
            finally { try { File.Delete(path); } catch { } }

            if (bgra is null || bgra.Length != (long)width * height * 4) return null;
            return Pack(bgra, width, height, factor);
        }
        finally { renderGate.Release(); }
    }

    private static BilevelResult Pack(byte[] bgra, int w, int h, int f)
    {
        int tw = (w + f - 1) / f, th = (h + f - 1) / f;
        var px = new byte[tw * th];
        int threshold = Math.Max(1, (int)Math.Ceiling(f * f * 0.4));
        for (int ty = 0; ty < th; ty++)
        {
            int y0 = ty * f, y1 = Math.Min(h, y0 + f);
            for (int tx = 0; tx < tw; tx++)
            {
                int x0 = tx * f, x1 = Math.Min(w, x0 + f), count = 0;
                for (int y = y0; y < y1; y++)
                {
                    int i = (y * w + x0) * 4;
                    for (int x = x0; x < x1; x++, i += 4) if (bgra[i] < 170) count++;
                }
                if (count >= threshold) px[ty * tw + tx] = 1;
            }
        }
        return new BilevelResult(Jbig2Encoder.EncodePdfStream(px, tw, th), tw, th);
    }

    /// <summary>이미지 한 장(w×h pt = 화소)을 그리는 임시 PDF를 만든다. 원본 스트림은 그대로 옮긴다.</summary>
    public static byte[] BuildTempPdf(PdfDictionary d)
    {
        var e = d.Elements;
        int w = e.GetInteger("/Width"), h = e.GetInteger("/Height");
        var dict = new StringBuilder("<< /Type /XObject /Subtype /Image");
        dict.Append(CultureInfo.InvariantCulture, $" /Width {w} /Height {h} /BitsPerComponent 1 /ColorSpace /DeviceGray");
        if (Resolve(e, "/Filter") is { } f) dict.Append(" /Filter ").Append(Ser(f));
        if (Resolve(e, "/DecodeParms") is { } dp) dict.Append(" /DecodeParms ").Append(Ser(dp));
        byte[] data = d.Stream!.Value;
        dict.Append(CultureInfo.InvariantCulture, $" /Length {data.Length} >>");

        using var ms = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void Obj(int n, string body) { offsets.Add(ms.Position); Write($"{n} 0 obj\n{body}\nendobj\n"); }
        Write("%PDF-1.5\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>"));
        string ops = FormattableString.Invariant($"q {w} 0 0 {h} 0 0 cm /Im0 Do Q\n");
        Obj(4, $"<< /Length {ops.Length} >>\nstream\n{ops}endstream");
        offsets.Add(ms.Position);
        Write($"5 0 obj\n{dict}\nstream\n");
        ms.Write(data);
        Write("\nendstream\nendobj\n");
        long xref = ms.Position;
        Write($"xref\n0 6\n0000000000 65535 f \n");
        foreach (long o in offsets) Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string Ser(PdfItem item)
    {
        if (item is PdfReference r) item = r.Value;
        switch (item)
        {
            case PdfName n: return n.Value;
            case PdfBoolean b: return b.Value ? "true" : "false";
            case PdfInteger i: return i.Value.ToString(CultureInfo.InvariantCulture);
            case PdfReal re: return re.Value.ToString(CultureInfo.InvariantCulture);
            case PdfNull: return "null";
            case PdfArray a: return "[" + string.Join(" ", a.Elements.Select(x => Ser(x))) + "]";
            case PdfDictionary d: return "<< " + string.Join(" ", d.Elements.Keys.Select(k => k + " " + Ser(d.Elements[k]!))) + " >>";
            default: return "null";
        }
    }

    private static PdfItem? Resolve(PdfDictionary.DictionaryElements e, string key)
        => e.ContainsKey(key) ? (e[key] is PdfReference r ? r.Value : e[key]) : null;

    private static bool Flag(PdfDictionary.DictionaryElements e, string key) => Resolve(e, key) is PdfBoolean { Value: true };
}
