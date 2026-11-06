using System.IO.Compression;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Optimize;

/// <summary>
/// 내용을 바꾸지 않는 정리(용량 줄이기 2단계). PDFsharp이 문서를 다시 쓸 때 카탈로그에서 닿는 객체만 쓰므로,
/// 증분 저장으로 쌓인 옛 객체와 어디서도 참조하지 않는 객체가 사라진다. 압축하지 않은 스트림은 Flate로 압축한다.
/// 여러 조각으로 나뉜 쪽 본문은 한 덩어리로 합치고, Flate 스트림(쪽 본문·글꼴 등)은 가장 작게 다시 압축한다. 글자와 선은 그대로 남는다.
/// 쪽을 새 문서로 옮기지 않으므로 책갈피·양식·쪽 이름표 같은 문서 정보가 그대로 남는다.
/// </summary>
public static class LosslessCleaner
{
    public static byte[] Clean(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        foreach (PdfPage page in doc.Pages) MergeContents(doc, page);
        RecompressFlateStreams(doc);
        doc.Options.CompressContentStreams = true;
        doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        using var output = new MemoryStream();
        doc.Save(output, closeStream: false);
        return output.ToArray();
    }

    /// <summary>여러 조각인 쪽 본문을 줄바꿈으로 이어 한 스트림으로 만든다(조각 경계에서 명령이 끊기지 않게 한다). 읽을 수 없으면 그대로 둔다.</summary>
    private static void MergeContents(PdfDocument doc, PdfPage page)
    {
        var parts = new List<byte[]>();
        try
        {
            var c = page.Elements["/Contents"];
            if (c is PdfReference r) c = r.Value;
            if (c is not PdfArray arr || arr.Elements.Count < 2) return;
            foreach (var item in arr.Elements)
            {
                var d = (item as PdfReference)?.Value as PdfDictionary;
                if (d?.Stream is null) return;
                parts.Add(d.Stream.UnfilteredValue);
            }
        }
        catch (Exception) { return; }

        using var ms = new MemoryStream();
        foreach (var p in parts) { ms.Write(p); ms.WriteByte((byte)'\n'); }
        var merged = new PdfDictionary(doc);
        doc.Internals.AddObject(merged);
        merged.CreateStream(Deflate(ms.ToArray()));
        merged.Elements.SetName("/Filter", "/FlateDecode");
        page.Elements["/Contents"] = merged.Reference!;
    }

    /// <summary>필터가 Flate 하나뿐인 스트림(이미지 제외)을 가장 작게 다시 압축한다. 더 작아질 때만 바꾼다.</summary>
    private static void RecompressFlateStreams(PdfDocument doc)
    {
        foreach (var obj in doc.Internals.GetAllObjects())
        {
            if (obj is not PdfDictionary d || d.Stream is null) continue;
            string? type = Name(d, "/Type"), subtype = Name(d, "/Subtype");
            if (subtype == "/Image" || type == "/ObjStm" || type == "/XRef") continue;
            if (!IsPlainFlate(d)) continue;
            try
            {
                byte[] raw = d.Stream.UnfilteredValue;
                byte[] packed = Deflate(raw);
                if (packed.Length >= d.Stream.Length) continue;
                d.Stream.Value = packed;
                d.Elements.SetName("/Filter", "/FlateDecode");
            }
            catch (Exception) { /* 읽을 수 없는 스트림은 그대로 둔다 */ }
        }
    }

    private static bool IsPlainFlate(PdfDictionary d)
    {
        if (d.Elements.ContainsKey("/DecodeParms") || d.Elements.ContainsKey("/DP")) return false;
        var f = d.Elements["/Filter"];
        if (f is PdfReference r) f = r.Value;
        return f switch
        {
            PdfName n => n.Value == "/FlateDecode",
            PdfArray a => a.Elements.Count == 1 && (a.Elements[0] as PdfName)?.Value == "/FlateDecode",
            _ => false,
        };
    }

    private static string? Name(PdfDictionary d, string key) => d.Elements[key] is PdfName n ? n.Value : null;

    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }
}
