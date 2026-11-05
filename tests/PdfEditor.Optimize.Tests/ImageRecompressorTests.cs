using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SharpDoc = PdfSharp.Pdf.PdfDocument;

namespace PdfEditor.Optimize.Tests;

public class ImageRecompressorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-recomp-" + Guid.NewGuid().ToString("N"));
    private static readonly LadderLevel Strongest = SizeLadder.Image[^1];   // 96 dpi, 품질 45

    public ImageRecompressorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private static SharpDoc Open(string path) => PdfReader.Open(path, PdfDocumentOpenMode.Modify);

    private static List<PdfDictionary> Images(SharpDoc doc)
    {
        var list = new List<PdfDictionary>();
        var seen = new HashSet<int>();
        foreach (PdfPage page in doc.Pages)
        {
            var xo = page.Resources?.Elements.GetDictionary("/XObject");
            if (xo is null) continue;
            foreach (var key in xo.Elements.Keys)
                if (xo.Elements.GetDictionary(key) is { } d && d.Elements.GetName("/Subtype") == "/Image" && seen.Add(d.Reference!.ObjectNumber)) list.Add(d);
        }
        return list;
    }

    [Fact]
    public async Task Recompress_LargeFlateRgb_ShrinksBytes_PdfiumCanRenderIt()
    {
        string src = P("flate.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(1200, 900, ImageKind.FlateRgb, 200, 150));   // 432 dpi
        string outPath = P("flate-out.pdf");
        using (var doc = Open(src))
        {
            var stats = ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None);
            Assert.Equal(1, stats.Images);
            Assert.Equal(1, stats.Replaced);
            Assert.True(stats.BytesAfter < stats.BytesBefore / 5, $"{stats.BytesBefore} -> {stats.BytesAfter}");
            var img = Images(doc).Single();
            Assert.Equal("/DCTDecode", img.Elements.GetName("/Filter"));
            Assert.True(img.Elements.GetInteger("/Width") <= 280, "downsampled to about 96 dpi");
            doc.Save(outPath);
        }
        using var t = new Pdf.Native.PdfiumThread();
        var pixels = await t.InvokeAsync(() =>
        {
            using var pdfium = Pdf.Native.PdfDocument.Open(t, outPath, null);
            using var page = pdfium.LoadPage(0);
            return page.RenderTile(1.0, 50, 592, 200, 150, false);   // 쪽(612×792)에서 이미지가 그려지는 영역: 왼쪽 50, 위에서 592
        });
        Assert.Contains(pixels, b => b != 255);   // 이미지가 실제로 그려졌다(흰 쪽이 아니다)
    }

    [Fact]
    public void Recompress_Jpeg_ShrinksBytes()
    {
        string src = P("jpeg.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(1000, 800, ImageKind.Jpeg, 100, 80));   // 720 dpi
        using var doc = Open(src);
        var stats = ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None);
        Assert.Equal(1, stats.Replaced);
        Assert.True(stats.BytesAfter < stats.BytesBefore / 4, $"{stats.BytesBefore} -> {stats.BytesAfter}");
        Assert.True(Images(doc).Single().Elements.GetInteger("/Width") <= 150);
    }

    [Fact]
    public void Recompress_FlateGray_StaysGray()
    {
        string src = P("gray.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(800, 600, ImageKind.FlateGray, 100, 75));
        using var doc = Open(src);
        var stats = ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None);
        Assert.Equal(1, stats.Replaced);
        Assert.Equal("/DeviceGray", Images(doc).Single().Elements.GetName("/ColorSpace"));
    }

    [Fact]
    public void Recompress_NeverUpscales()
    {
        string src = P("small.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(100, 100, ImageKind.FlateRgb, 500, 500));   // 14 dpi: 이미 낮다
        using var doc = Open(src);
        ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None);
        Assert.True(Images(doc).Single().Elements.GetInteger("/Width") <= 100);
    }

    [Fact]
    public void Recompress_SharedImage_ProcessedOnce()
    {
        string src = P("shared.pdf");
        ImagePdfFactory.Create(src,
            new ImageSpec(800, 600, ImageKind.FlateRgb, 100, 75, SharedOnNextPage: true),
            new ImageSpec(800, 600, ImageKind.FlateRgb, 100, 75));
        using var doc = Open(src);
        var stats = ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None);
        Assert.Equal(1, stats.Images);
        Assert.Single(Images(doc));
    }

    [Fact]
    public void Recompress_SoftMaskImage_KeepsMask()
    {
        string src = P("mask.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(600, 400, ImageKind.FlateRgb, 100, 66, WithSoftMask: true));
        using var doc = Open(src);
        ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None);
        Assert.True(Images(doc).Single().Elements.ContainsKey("/SMask"));
    }

    [Fact]
    public void Recompress_ArrayColorSpace_IsSkippedNotThrown()
    {
        // 실제 PDF에서 흔한 /ColorSpace [/ICCBased …]·[/Indexed …]: 이름이 아니라 배열이다.
        string src = P("indexed.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(600, 400, ImageKind.FlateRgb, 100, 66));
        using var doc = Open(src);
        var img = Images(doc).Single();
        img.Elements["/ColorSpace"] = new PdfArray(doc, new PdfName("/Indexed"), new PdfName("/DeviceRGB"), new PdfInteger(255), new PdfString("x"));
        long before = img.Stream!.Value.Length;
        var stats = ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None);
        Assert.Equal(1, stats.Images);
        Assert.Equal(1, stats.Skipped);
        Assert.Equal(0, stats.Replaced);
        Assert.Equal(before, Images(doc).Single().Stream!.Value.Length);
    }

    private static PdfArray IccBased(SharpDoc doc, int n)
    {
        var icc = new PdfDictionary(doc);
        icc.Elements["/N"] = new PdfInteger(n);
        icc.CreateStream(new byte[] { 0 });
        doc.Internals.AddObject(icc);
        return new PdfArray(doc, new PdfName("/ICCBased"), icc.Reference!);
    }

    [Fact]
    public void Recompress_IccBasedRgb_IsRecompressed_ColorSpaceKept()
    {
        string src = P("icc3.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(800, 600, ImageKind.FlateRgb, 100, 75));
        using var doc = Open(src);
        var img = Images(doc).Single();
        img.Elements["/ColorSpace"] = IccBased(doc, 3);
        var stats = ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None);
        Assert.Equal(1, stats.Replaced);
        var after = Images(doc).Single();
        Assert.Equal("/DCTDecode", after.Elements.GetName("/Filter"));
        Assert.IsType<PdfArray>(after.Elements["/ColorSpace"]);   // 프로필 배열은 그대로
    }

    [Fact]
    public void Recompress_IccBasedGray_IsRecompressed_AsGrayJpeg()
    {
        string src = P("icc1.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(800, 600, ImageKind.FlateGray, 100, 75));
        using var doc = Open(src);
        Images(doc).Single().Elements["/ColorSpace"] = IccBased(doc, 1);
        var stats = ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None);
        Assert.Equal(1, stats.Replaced);
        Assert.IsType<PdfArray>(Images(doc).Single().Elements["/ColorSpace"]);
    }

    [Fact]
    public void Recompress_IccBasedCmyk_IsSkipped()
    {
        string src = P("icc4.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(800, 600, ImageKind.FlateRgb, 100, 75));
        using var doc = Open(src);
        Images(doc).Single().Elements["/ColorSpace"] = IccBased(doc, 4);
        Assert.Equal(1, ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None).Skipped);
    }

    private static void SetPredicted(SharpDoc doc, PdfDictionary img, int predictor, int w, int h)
    {
        byte[] raw = img.Stream!.UnfilteredValue;   // w*h*3
        int rowBytes = w * 3;
        var predicted = new byte[h * (rowBytes + 1)];
        for (int y = 0; y < h; y++)
        {
            predicted[y * (rowBytes + 1)] = 0;   // PNG 필터 0(없음)
            Buffer.BlockCopy(raw, y * rowBytes, predicted, y * (rowBytes + 1) + 1, rowBytes);
        }
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(predicted);
        img.Stream.Value = ms.ToArray();
        var parms = new PdfDictionary(doc);
        parms.Elements["/Predictor"] = new PdfInteger(predictor);
        parms.Elements["/Colors"] = new PdfInteger(3);
        parms.Elements["/BitsPerComponent"] = new PdfInteger(8);
        parms.Elements["/Columns"] = new PdfInteger(w);
        img.Elements["/DecodeParms"] = parms;
    }

    [Fact]
    public void Recompress_FlateWithPngPredictor_IsRecompressed()
    {
        string src = P("png.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(600, 400, ImageKind.FlateRgb, 100, 66));
        using var doc = Open(src);
        SetPredicted(doc, Images(doc).Single(), 15, 600, 400);
        var stats = ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None);
        Assert.Equal(1, stats.Replaced);
        Assert.False(Images(doc).Single().Elements.ContainsKey("/DecodeParms"));
    }

    [Fact]
    public void Recompress_FlateWithTiffPredictor_IsSkipped()
    {
        string src = P("tiff.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(600, 400, ImageKind.FlateRgb, 100, 66));
        using var doc = Open(src);
        SetPredicted(doc, Images(doc).Single(), 2, 600, 400);
        Assert.Equal(1, ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None).Skipped);
    }

    [Fact]
    public void Recompress_ArrayFilter_IsSkippedNotThrown()
    {
        string src = P("filters.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(600, 400, ImageKind.FlateRgb, 100, 66));
        using var doc = Open(src);
        var img = Images(doc).Single();
        img.Elements["/Filter"] = new PdfArray(doc, new PdfName("/ASCII85Decode"), new PdfName("/FlateDecode"));
        var stats = ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None);
        Assert.Equal(1, stats.Skipped);
    }

    [Fact]
    public void Recompress_Cancelled_Throws()
    {
        string src = P("cancel.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(300, 200, ImageKind.FlateRgb, 100, 66));
        using var doc = Open(src);
        Assert.Throws<OperationCanceledException>(() => ImageRecompressor.Recompress(doc, Strongest, new CancellationToken(true)));
    }

    [Fact]
    public void Recompress_NoImages_ZeroStats()
    {
        string src = P("blank.pdf");
        TestPdfFactory.CreateBlank(src, 1, 200, 200);
        using var doc = Open(src);
        var stats = ImageRecompressor.Recompress(doc, Strongest, CancellationToken.None);
        Assert.Equal(0, stats.Images);
        Assert.Equal(0, stats.Replaced);
    }
}
