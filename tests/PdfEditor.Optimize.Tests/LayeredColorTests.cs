using Pdf.Native;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Optimize.Tests;

public sealed class LayeredColorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-layered-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public LayeredColorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    /// <summary>300×200pt 쪽: 빨간 사각형(50,50 크기 100×60), 노란 사각형(200,50 크기 60×60), 검정 굵은 선.</summary>
    private string ColorPage()
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Width = new XUnitPt(300);
        page.Height = new XUnitPt(200);
        using (var g = XGraphics.FromPdfPage(page))
        {
            g.DrawRectangle(new XSolidBrush(XColor.FromArgb(220, 20, 20)), 50, 50, 100, 60);
            g.DrawRectangle(new XSolidBrush(XColor.FromArgb(240, 220, 20)), 200, 50, 60, 60);
            g.DrawLine(new XPen(XColors.Black, 4), 20, 150, 280, 150);
        }
        string path = P("color.pdf");
        doc.Save(path);
        return path;
    }

    private async Task<byte[]> Render(string path, double scale, int w, int h) => await _t.InvokeAsync(() =>
    {
        using var doc = Pdf.Native.PdfDocument.Open(_t, path, null);
        using var page = doc.LoadPage(0);
        return page.RenderTile(scale, 0, 0, w, h, false);
    });

    private static (int R, int G, int B) Px(byte[] bgra, int w, int x, int y) => (bgra[(y * w + x) * 4 + 2], bgra[(y * w + x) * 4 + 1], bgra[(y * w + x) * 4]);

    [Fact]
    public async Task BuildLayered_KeepsColorsAndLines_AndIsMuchSmallerThanAFullColorImage()
    {
        string src = ColorPage();
        string outPath = P("layered.pdf");
        Rasterizer.BuildMonochrome(_t, src, null, outPath, new LadderLevel(300, 0), CancellationToken.None, keepColor: true);

        var bgra = await Render(outPath, 1.0, 300, 200);
        var red = Px(bgra, 300, 100, 80);
        var yellow = Px(bgra, 300, 230, 80);
        var white = Px(bgra, 300, 250, 180);
        var line = Px(bgra, 300, 150, 150);
        Assert.True(red.R > 180 && red.G < 90 && red.B < 90, $"red area {red}");
        Assert.True(yellow.R > 200 && yellow.G > 170 && yellow.B < 120, $"yellow area {yellow}");
        Assert.True(white.R > 240 && white.G > 240 && white.B > 240, $"white area {white}");
        Assert.True(line.R < 90 && line.G < 90 && line.B < 90, $"black line {line}");
    }

    [Fact]
    public void BuildLayered_PageIsAMaskedColorImage_WithJbig2Mask()
    {
        string src = ColorPage();
        string outPath = P("layered2.pdf");
        Rasterizer.BuildMonochrome(_t, src, null, outPath, new LadderLevel(300, 0), CancellationToken.None, keepColor: true);

        using var doc = PdfReader.Open(outPath, PdfDocumentOpenMode.Import);
        var fg = doc.Pages[0].Resources!.Elements.GetDictionary("/XObject")!.Elements.GetDictionary("/Im0")!;
        Assert.Equal("/DCTDecode", fg.Elements.GetName("/Filter"));
        Assert.Equal("/DeviceRGB", fg.Elements.GetName("/ColorSpace"));
        var mask = fg.Elements.GetDictionary("/Mask")!;
        Assert.Equal("/JBIG2Decode", mask.Elements.GetName("/Filter"));
        Assert.True(mask.Elements.GetBoolean("/ImageMask"));
        Assert.True(mask.Elements.GetInteger("/Width") > fg.Elements.GetInteger("/Width"));   // 마스크가 컬러 층보다 해상도가 높다
    }

    [Fact]
    public void Verify_LayeredColorOutput_PassesWithoutBinarizing()
    {
        string src = ColorPage();
        string outPath = P("layered3.pdf");
        Rasterizer.BuildMonochrome(_t, src, null, outPath, new LadderLevel(300, 0), CancellationToken.None, keepColor: true);
        Assert.True(OutputVerifier.Verify(_t, src, outPath, null, 150));
    }
}
