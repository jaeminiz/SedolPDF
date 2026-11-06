using System.IO.Compression;
using Pdf.Native;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Optimize.Tests;

public sealed class BilevelRecompressionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-bilevel-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public BilevelRecompressionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>1비트 Flate DeviceGray 이미지 한 장(px×py 화소)을 drawW×drawH pt로 그린 쪽. 화소 1(검정)을 0으로 저장한다(DeviceGray 0 = 검정).</summary>
    private string ScanPdf(int px, int py, double drawW, double drawH, bool imageMask = false)
    {
        byte[] bits = Jbig2EncoderTests.Drawing(px, py);
        int stride = (px + 7) / 8;
        var packed = new byte[stride * py];
        for (int y = 0; y < py; y++)
            for (int x = 0; x < px; x++)
                if (bits[y * px + x] == 0) packed[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));   // 흰색 = 1
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, true)) z.Write(packed);

        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Width = new XUnitPt(612);
        page.Height = new XUnitPt(792);
        var img = new PdfDictionary(doc);
        img.Elements["/Type"] = new PdfName("/XObject");
        img.Elements["/Subtype"] = new PdfName("/Image");
        img.Elements["/Width"] = new PdfInteger(px);
        img.Elements["/Height"] = new PdfInteger(py);
        img.Elements["/BitsPerComponent"] = new PdfInteger(1);
        if (imageMask) img.Elements["/ImageMask"] = new PdfBoolean(true); else img.Elements["/ColorSpace"] = new PdfName("/DeviceGray");
        img.Elements["/Filter"] = new PdfName("/FlateDecode");
        img.CreateStream(ms.ToArray());
        doc.Internals.AddObject(img);
        var xo = new PdfDictionary(doc);
        xo.Elements["/Im0"] = img.Reference!;
        page.Resources.Elements["/XObject"] = xo;
        page.Contents.AppendContent().CreateStream(System.Text.Encoding.ASCII.GetBytes(FormattableString.Invariant($"q {drawW} 0 0 {drawH} 50 50 cm /Im0 Do Q\n")));
        string path = Path.Combine(_dir, imageMask ? "mask.pdf" : "scan.pdf");
        doc.Save(path);
        return path;
    }

    private static PdfDictionary FirstImage(PdfSharp.Pdf.PdfDocument doc)
    {
        var xo = doc.Pages[0].Resources!.Elements.GetDictionary("/XObject")!;
        return xo.Elements.GetDictionary("/Im0")!;
    }

    [Fact]
    public void Recompress_BilevelScan_BecomesSmallerJbig2AtHalfSize()
    {
        string src = ScanPdf(2400, 1700, 288, 204);   // 600dpi
        using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
        long before = FirstImage(doc).Stream.Length;
        var stats = ImageRecompressor.Recompress(doc, SizeLadder.Image[0], CancellationToken.None, _t, new BilevelCache());

        var d = FirstImage(doc);
        Assert.Equal(1, stats.Replaced);
        Assert.Equal("/JBIG2Decode", d.Elements.GetName("/Filter"));
        Assert.Equal(1200, d.Elements.GetInteger("/Width"));   // 600dpi → 300dpi
        Assert.Equal(850, d.Elements.GetInteger("/Height"));
        Assert.Equal(1, d.Elements.GetInteger("/BitsPerComponent"));
        Assert.True(d.Stream.Length < before, $"{before} -> {d.Stream.Length}");
    }

    [Fact]
    public async Task Recompress_BilevelScan_StillRendersTheSameDrawing()
    {
        string src = ScanPdf(2400, 1700, 288, 204);
        string outPath = Path.Combine(_dir, "out.pdf");
        using (var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify))
        {
            ImageRecompressor.Recompress(doc, SizeLadder.Image[0], CancellationToken.None, _t, new BilevelCache());
            doc.Save(outPath);
        }
        Assert.True(OutputVerifier.Verify(_t, src, outPath, null, null), "reduced scan should still look like the original");
        await Task.CompletedTask;
    }

    [Fact]
    public void Recompress_WithoutThread_LeavesBilevelImagesAlone()
    {
        string src = ScanPdf(2400, 1700, 288, 204);
        using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
        var stats = ImageRecompressor.Recompress(doc, SizeLadder.Image[0], CancellationToken.None);
        Assert.Equal(0, stats.Replaced);
        Assert.Equal("/FlateDecode", FirstImage(doc).Elements.GetName("/Filter"));
    }

    [Fact]
    public void Recompress_ImageMask_IsNotTouched()
    {
        string src = ScanPdf(2400, 1700, 288, 204, imageMask: true);
        using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
        var stats = ImageRecompressor.Recompress(doc, SizeLadder.Image[0], CancellationToken.None, _t, new BilevelCache());
        Assert.Equal(0, stats.Replaced);
    }

    [Fact]
    public void Recompress_LowerLevels_KeepAtLeast200Dpi()
    {
        string src = ScanPdf(2400, 1700, 288, 204);   // 600dpi, 단계 96dpi여도 1비트는 200dpi까지만
        using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
        ImageRecompressor.Recompress(doc, SizeLadder.Image[^1], CancellationToken.None, _t, new BilevelCache());
        Assert.Equal(800, FirstImage(doc).Elements.GetInteger("/Width"));   // 600 / 3
    }
}
