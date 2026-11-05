using PdfEditor.TestFixtures;
using Pdf.Native;

namespace PdfEditor.Optimize.Tests;

public sealed class RasterizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-raster-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public RasterizerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private Task<List<(float W, float H)>> PageSizes(string path) => _t.InvokeAsync(() =>
    {
        using var doc = Pdf.Native.PdfDocument.Open(_t, path, null);
        return Enumerable.Range(0, doc.PageCount).Select(i => { var s = doc.GetPageSizePt(i); return (s.Width, s.Height); }).ToList();
    });

    private Task<int> TextChars(string path) => _t.InvokeAsync(() =>
    {
        using var doc = Pdf.Native.PdfDocument.Open(_t, path, null);
        using var page = doc.LoadPage(0);
        using var text = page.LoadText();
        return text.CharCount;
    });

    [Fact]
    public async Task Build_PageCountAndSizeMatchSource()
    {
        string src = P("src.pdf");
        TestPdfFactory.CreateLabeled(src, [(300, 400), (500, 300), (612, 792)]);
        string outPath = P("out.pdf");
        Rasterizer.Build(_t, src, null, outPath, SizeLadder.Raster[^1], CancellationToken.None);

        var a = await PageSizes(src);
        var b = await PageSizes(outPath);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].W, b[i].W, 1.0);
            Assert.Equal(a[i].H, b[i].H, 1.0);
        }
    }

    [Fact]
    public async Task Build_RotatedPage_KeepsDisplayedSize()
    {
        string src = P("rot.pdf");
        TestPdfFactory.CreateTextLines(src, ["rotated"], rotate: 90);
        string outPath = P("rot-out.pdf");
        Rasterizer.Build(_t, src, null, outPath, SizeLadder.Raster[^1], CancellationToken.None);
        var a = (await PageSizes(src)).Single();
        var b = (await PageSizes(outPath)).Single();
        Assert.Equal(a.W, b.W, 1.0);
        Assert.Equal(a.H, b.H, 1.0);
    }

    [Fact]
    public async Task Build_OutputHasNoTextLayer()
    {
        string src = P("text.pdf");
        TestPdfFactory.CreateTextLines(src, ["HELLO WORLD", "second line"]);
        Assert.True(await TextChars(src) > 0, "the source has text");
        string outPath = P("text-out.pdf");
        Rasterizer.Build(_t, src, null, outPath, SizeLadder.Raster[^1], CancellationToken.None);
        Assert.Equal(0, await TextChars(outPath));
    }

    [Fact]
    public void Build_LowerLevel_SmallerFile()
    {
        string src = P("noise.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(1000, 800, ImageKind.FlateRgb, 500, 400));
        string mild = P("mild.pdf"), strong = P("strong.pdf");
        Rasterizer.Build(_t, src, null, mild, SizeLadder.Raster[0], CancellationToken.None);
        Rasterizer.Build(_t, src, null, strong, SizeLadder.Raster[^1], CancellationToken.None);
        Assert.True(new FileInfo(strong).Length < new FileInfo(mild).Length, $"{new FileInfo(mild).Length} vs {new FileInfo(strong).Length}");
    }

    [Fact]
    public void Build_Cancelled_Throws_AndWritesNothing()
    {
        string src = P("c.pdf");
        TestPdfFactory.CreateBlank(src, 2, 200, 200);
        string outPath = P("c-out.pdf");
        Assert.Throws<OperationCanceledException>(() => Rasterizer.Build(_t, src, null, outPath, SizeLadder.Raster[0], new CancellationToken(true)));
        Assert.False(File.Exists(outPath));
    }
}
