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

    public static void Build(PdfiumThread t, string sourcePath, string? password, string outPath, LadderLevel level, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var src = t.InvokeAsync(() => Pdf.Native.PdfDocument.Open(t, sourcePath, password), ct).GetAwaiter().GetResult();
        try
        {
            int pageCount = t.InvokeAsync(() => src.PageCount, ct).GetAwaiter().GetResult();
            using var outDoc = new PdfSharp.Pdf.PdfDocument();
            for (int i = 0; i < pageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                int index = i;
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

    private static void AddImagePage(PdfSharp.Pdf.PdfDocument doc, double widthPt, double heightPt, int pw, int ph, byte[] jpeg)
    {
        var page = doc.AddPage();
        page.Width = new XUnitPt(widthPt);
        page.Height = new XUnitPt(heightPt);
        var img = new PdfDictionary(doc);
        img.Elements["/Type"] = new PdfName("/XObject");
        img.Elements["/Subtype"] = new PdfName("/Image");
        img.Elements["/Width"] = new PdfInteger(pw);
        img.Elements["/Height"] = new PdfInteger(ph);
        img.Elements["/BitsPerComponent"] = new PdfInteger(8);
        img.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
        img.Elements["/Filter"] = new PdfName("/DCTDecode");
        img.CreateStream(jpeg);
        doc.Internals.AddObject(img);
        var xo = new PdfDictionary(doc);
        xo.Elements["/Im0"] = img.Reference!;
        page.Resources.Elements["/XObject"] = xo;
        string ops = FormattableString.Invariant($"q {widthPt:0.####} 0 0 {heightPt:0.####} 0 0 cm /Im0 Do Q\n");
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(ops));
    }
}
