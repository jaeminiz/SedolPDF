using System.Runtime.InteropServices;
using System.Text;
using Pdf.Native;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace PdfEditor.Optimize;

/// <summary>
/// 래스터화(용량 줄이기 마지막 수단): 쪽마다 PDFium으로 그려 JPEG으로 압축한 이미지 한 장짜리 새 PDF를 만든다.
/// 쪽 크기(회전을 적용한 보이는 크기)는 그대로, 글자·링크·주석 객체는 사라진다(그려진 모양만 남는다).
/// 호출 스레드는 PDFium 전용 스레드가 아니어야 한다(PDFium 일은 쪽마다 전용 스레드로 보낸다).
/// </summary>
public static class Rasterizer
{
    /// <summary>쪽 하나의 최대 픽셀 수. A0 같은 큰 쪽이 메모리를 터뜨리지 않게 dpi를 낮춘다.</summary>
    private const long MaxPixels = 40_000_000;

    /// <summary>색 유지 변환에서 가장 어두운 색 성분이 이 값 미만이면 잉크다. 검증도 같은 기준으로 원본을 2값으로 만든다.</summary>
    internal const int InkThreshold = 190;

    public static void Build(PdfiumThread t, string sourcePath, string? password, string outPath, LadderLevel level, CancellationToken ct, IReadOnlyList<int>? pages = null)
    {
        ct.ThrowIfCancellationRequested();
        var src = t.InvokeAsync(() => Pdf.Native.PdfDocument.Open(t, sourcePath, password), ct).GetAwaiter().GetResult();
        try
        {
            int sourcePages = t.InvokeAsync(() => src.PageCount, ct).GetAwaiter().GetResult();
            int[] order = pages is null ? Enumerable.Range(0, sourcePages).ToArray() : pages.Where(p => p >= 0 && p < sourcePages).ToArray();
            int pageCount = order.Length;
            using var outDoc = new PdfSharp.Pdf.PdfDocument();
            for (int i = 0; i < pageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                int index = order[i];
                var r = t.InvokeAsync(() =>
                {
                    using var page = src.LoadPage(index);
                    var size = page.SizePt;
                    var (scale, pw, ph) = PixelSize(size.Width, size.Height, level.MaxDpi);
                    return (size.Width, size.Height, pw, ph, Pixels: page.RenderTile(scale, 0, 0, pw, ph, true));
                }, ct).GetAwaiter().GetResult();

                byte[] jpeg = EncodeJpeg(r.Pixels, r.pw, r.ph, level.JpegQuality);
                AddImagePage(outDoc, r.Width, r.Height, r.pw, r.ph, jpeg);
            }
            ct.ThrowIfCancellationRequested();
            outDoc.Save(outPath);
        }
        finally { src.Dispose(); }
    }

    /// <summary>
    /// 흑백 변환: 쪽마다 PDFium으로 그려 어두운 화소(어느 색 성분이든 170 미만)를 검정으로 하는 1비트 그림을 JBIG2로 압축한 이미지 한 장짜리 새 PDF를 만든다.
    /// 선 위주의 도면에 알맞고 색·음영·글자 객체는 사라진다. 쪽 크기는 그대로다.
    /// </summary>
    public static void BuildMonochrome(PdfiumThread t, string sourcePath, string? password, string outPath, LadderLevel level, CancellationToken ct, IProgress<double>? progress = null,
        bool keepColor = false, IReadOnlyList<int>? pages = null)
    {
        ct.ThrowIfCancellationRequested();
        var src = t.InvokeAsync(() => Pdf.Native.PdfDocument.Open(t, sourcePath, password), ct).GetAwaiter().GetResult();
        try
        {
            int sourcePages = t.InvokeAsync(() => src.PageCount, ct).GetAwaiter().GetResult();
            int[] order = pages is null ? Enumerable.Range(0, sourcePages).ToArray() : pages.Where(p => p >= 0 && p < sourcePages).ToArray();
            int pageCount = order.Length;
            // 그리기는 PDFium 스레드에서 차례로, JBIG2 압축은 여러 쪽을 동시에(큰 쪽은 메모리가 크므로 동시 3쪽까지).
            var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount - 1, 1, 3));
            var encoded = new Task<(double W, double H, int Pw, int Ph, byte[] Jbig2, byte[]? Fg, int Fw, int Fh)>[pageCount];
            int done = 0;
            try
            {
                for (int i = 0; i < pageCount; i++)
                {
                    gate.Wait(ct);
                    int index = order[i];
                    (double Width, double Height, int pw, int ph, byte[] Pixels) r;
                    try
                    {
                        r = t.InvokeAsync(() =>
                        {
                            using var page = src.LoadPage(index);
                            var size = page.SizePt;
                            var (scale, pw, ph) = PixelSize(size.Width, size.Height, level.MaxDpi);
                            return ((double)size.Width, (double)size.Height, pw, ph, page.RenderTile(scale, 0, 0, pw, ph, true));
                        }, ct).GetAwaiter().GetResult();
                    }
                    catch { gate.Release(); throw; }
                    encoded[i] = Task.Run(() =>
                    {
                        try
                        {
                            var (bits, fg, fw, fh) = keepColor ? Layers(r.Pixels, r.pw, r.ph, level.MaxDpi) : (Binarize(r.Pixels, r.pw * r.ph), null, 0, 0);
                            var jb = Jbig2Encoder.EncodePdfStream(bits, r.pw, r.ph);
                            progress?.Report((double)Interlocked.Increment(ref done) / pageCount);
                            return (r.Width, r.Height, r.pw, r.ph, jb, fg, fw, fh);
                        }
                        finally { gate.Release(); }
                    }, CancellationToken.None);
                }
            }
            finally
            {
                // 취소·오류여도 이미 시작한 압축이 끝날 때까지 기다린 뒤(파일·메모리 정리) 나간다.
                foreach (var task in encoded) { try { task?.GetAwaiter().GetResult(); } catch { } }
            }

            using var outDoc = new PdfSharp.Pdf.PdfDocument();
            foreach (var task in encoded)
            {
                var (w, h, pw, ph, jb, fg, fw, fh) = task.GetAwaiter().GetResult();
                if (fg is null) AddImagePage(outDoc, w, h, pw, ph, jb, mono: true);
                else AddLayeredPage(outDoc, w, h, pw, ph, jb, fg, fw, fh);
            }
            ct.ThrowIfCancellationRequested();
            outDoc.Save(outPath);
        }
        finally { src.Dispose(); }
    }

    /// <summary>어느 색 성분이든 170 미만이면 검정(1)으로 하는 순수 흑백 비트.</summary>
    private static byte[] Binarize(byte[] bgra, int count)
    {
        var bits = new byte[count];
        for (int p = 0; p < count; p++)
        {
            int o = p * 4;
            if (bgra[o] < 170 || bgra[o + 1] < 170 || bgra[o + 2] < 170) bits[p] = 1;
        }
        return bits;
    }

    /// <summary>
    /// 색 유지 변환: 먹(잉크) 화소(가장 어두운 색 성분이 190 미만)를 1로 하는 고해상도 마스크와, 약 100dpi로 줄인 색층(블록마다 잉크 화소의 평균색, 가장자리 번짐을 피하려고 진한 화소를 우선)을 만든다.
    /// 잉크가 없는 블록은 검정으로 채운다(JPEG이 작아진다).
    /// </summary>
    private static (byte[] Bits, byte[] FgJpeg, int Fw, int Fh) Layers(byte[] bgra, int w, int h, int dpi)
    {
        var bits = new byte[w * h];
        int f = Math.Max(1, (int)Math.Round(dpi / 100.0, MidpointRounding.AwayFromZero));
        int fw = (w + f - 1) / f, fh = (h + f - 1) / f;
        var fg = new byte[fw * fh * 4];
        for (int by = 0; by < fh; by++)
            for (int bx = 0; bx < fw; bx++)
            {
                long cr = 0, cg = 0, cb = 0, cn = 0, ar = 0, ag = 0, ab = 0, an = 0;
                for (int y = by * f; y < Math.Min(h, by * f + f); y++)
                    for (int x = bx * f; x < Math.Min(w, bx * f + f); x++)
                    {
                        int o = (y * w + x) * 4;
                        int b = bgra[o], g = bgra[o + 1], r = bgra[o + 2];
                        int min = Math.Min(b, Math.Min(g, r));
                        if (min >= InkThreshold) continue;
                        bits[y * w + x] = 1;
                        ar += r; ag += g; ab += b; an++;
                        if (min < 120) { cr += r; cg += g; cb += b; cn++; }
                    }
                int i = (by * fw + bx) * 4;
                long sr = cn > 0 ? cr / cn : an > 0 ? ar / an : 0, sg = cn > 0 ? cg / cn : an > 0 ? ag / an : 0, sb = cn > 0 ? cb / cn : an > 0 ? ab / an : 0;
                // 검정·회색 계열(채도가 낮음)은 검정으로 통일한다: 글자 가장자리의 회색 번짐이 색층에 잡음으로 들어가면 JPEG이 몇 배로 커진다.
                // 다만 블록 전체가 진한 색이 없는 회색 잉크이면(연한 회색 면) 그 회색을 그대로 둔다. 면이 검게 칠해지지 않게 한다.
                if (Math.Max(sr, Math.Max(sg, sb)) - Math.Min(sr, Math.Min(sg, sb)) < 45)
                {
                    int blockPixels = (Math.Min(h, by * f + f) - by * f) * (Math.Min(w, bx * f + f) - bx * f);
                    if (cn > 0 || an < blockPixels) sr = sg = sb = 0;
                }
                fg[i] = (byte)sb; fg[i + 1] = (byte)sg; fg[i + 2] = (byte)sr;
                fg[i + 3] = 255;
            }
        return (bits, EncodeJpeg(fg, fw, fh, 60), fw, fh);
    }

    /// <summary>쪽(pt)을 dpi로 그릴 때의 배율과 화소 크기. 쪽이 너무 크면(MaxPixels) 배율을 낮춘다. 검증(OutputVerifier)이 같은 값을 써서 같은 배율로 비교한다.</summary>
    internal static (double Scale, int Width, int Height) PixelSize(double widthPt, double heightPt, int dpi)
    {
        double scale = dpi / 72.0;
        long px = (long)Math.Ceiling(widthPt * scale) * (long)Math.Ceiling(heightPt * scale);
        if (px > MaxPixels) scale *= Math.Sqrt((double)MaxPixels / px);
        return (scale, Math.Max(1, (int)Math.Ceiling(widthPt * scale)), Math.Max(1, (int)Math.Ceiling(heightPt * scale)));
    }

    private static byte[] EncodeJpeg(byte[] bgra, int w, int h, int quality)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        if (bmp.RowBytes != w * 4 || bgra.Length != w * h * 4) throw new InvalidOperationException("Unexpected bitmap layout.");
        Marshal.Copy(bgra, 0, bmp.GetPixels(), bgra.Length);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality) ?? throw new InvalidOperationException("JPEG encoding failed.");
        return data.ToArray();
    }

    /// <summary>색층(저해상도 컬러 JPEG)을 고해상도 1비트 마스크(JBIG2)로 뚫어 그리는 쪽. 마스크가 0(잉크)인 곳만 색층이 보이고 나머지는 흰 종이다.</summary>
    private static void AddLayeredPage(PdfSharp.Pdf.PdfDocument doc, double widthPt, double heightPt, int pw, int ph, byte[] jbig2, byte[] fgJpeg, int fw, int fh)
    {
        var page = doc.AddPage();
        page.Width = new XUnitPt(widthPt);
        page.Height = new XUnitPt(heightPt);
        var mask = new PdfDictionary(doc);
        mask.Elements["/Type"] = new PdfName("/XObject");
        mask.Elements["/Subtype"] = new PdfName("/Image");
        mask.Elements["/Width"] = new PdfInteger(pw);
        mask.Elements["/Height"] = new PdfInteger(ph);
        mask.Elements["/ImageMask"] = new PdfBoolean(true);
        mask.Elements["/BitsPerComponent"] = new PdfInteger(1);
        mask.Elements["/Filter"] = new PdfName("/JBIG2Decode");
        mask.CreateStream(jbig2);
        doc.Internals.AddObject(mask);
        var img = new PdfDictionary(doc);
        img.Elements["/Type"] = new PdfName("/XObject");
        img.Elements["/Subtype"] = new PdfName("/Image");
        img.Elements["/Width"] = new PdfInteger(fw);
        img.Elements["/Height"] = new PdfInteger(fh);
        img.Elements["/BitsPerComponent"] = new PdfInteger(8);
        img.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
        img.Elements["/Filter"] = new PdfName("/DCTDecode");
        img.Elements["/Mask"] = mask.Reference!;
        img.CreateStream(fgJpeg);
        doc.Internals.AddObject(img);
        var xo = new PdfDictionary(doc);
        xo.Elements["/Im0"] = img.Reference!;
        page.Resources.Elements["/XObject"] = xo;
        string ops = FormattableString.Invariant($"q {widthPt:0.####} 0 0 {heightPt:0.####} 0 0 cm /Im0 Do Q\n");
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(ops));
    }

    private static void AddImagePage(PdfSharp.Pdf.PdfDocument doc, double widthPt, double heightPt, int pw, int ph, byte[] jpeg, bool mono = false)
    {
        var page = doc.AddPage();
        page.Width = new XUnitPt(widthPt);
        page.Height = new XUnitPt(heightPt);
        var img = new PdfDictionary(doc);
        img.Elements["/Type"] = new PdfName("/XObject");
        img.Elements["/Subtype"] = new PdfName("/Image");
        img.Elements["/Width"] = new PdfInteger(pw);
        img.Elements["/Height"] = new PdfInteger(ph);
        img.Elements["/BitsPerComponent"] = new PdfInteger(mono ? 1 : 8);
        img.Elements["/ColorSpace"] = new PdfName(mono ? "/DeviceGray" : "/DeviceRGB");
        img.Elements["/Filter"] = new PdfName(mono ? "/JBIG2Decode" : "/DCTDecode");
        img.CreateStream(jpeg);
        doc.Internals.AddObject(img);
        var xo = new PdfDictionary(doc);
        xo.Elements["/Im0"] = img.Reference!;
        page.Resources.Elements["/XObject"] = xo;
        string ops = FormattableString.Invariant($"q {widthPt:0.####} 0 0 {heightPt:0.####} 0 0 cm /Im0 Do Q\n");
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(ops));
    }
}
