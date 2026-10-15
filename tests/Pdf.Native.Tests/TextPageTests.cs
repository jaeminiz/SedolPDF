using PdfEditor.TestFixtures;

namespace Pdf.Native.Tests;

public class TextPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-tp-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public TextPageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task FindAll_English_ReturnsMatchesAndRects()
    {
        var path = P("lab.pdf");
        TestPdfFactory.CreateLabeled(path, [(300, 400), (300, 400), (300, 400)]);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(1);
            using var tp = page.LoadText();
            var m = tp.FindAll("PAGE 2", matchCase: false, wholeWord: false);
            Assert.Single(m);
            Assert.NotEmpty(tp.GetRects(m[0].CharIndex, m[0].CharCount));
            Assert.Equal("PAGE 2", tp.GetText(m[0].CharIndex, m[0].CharCount));
        });
    }

    [Fact]
    public async Task FindAll_Korean_Works()
    {
        var path = P("ko.pdf");
        TestPdfFactory.CreateKoreanText(path, "배관 계통도 검토");
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            using var tp = page.LoadText();
            var m = tp.FindAll("계통도", false, false);
            Assert.Single(m);
            Assert.Equal("계통도", tp.GetText(m[0].CharIndex, m[0].CharCount));
        });
    }

    [Fact]
    public async Task FindAll_MatchCase_Respected()
    {
        var path = P("lab.pdf");
        TestPdfFactory.CreateLabeled(path, [(300, 400), (300, 400)]);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(1);
            using var tp = page.LoadText();
            Assert.Single(tp.FindAll("page 2", false, false));
            Assert.Empty(tp.FindAll("page 2", true, false));
        });
    }

    [Fact]
    public async Task FindAll_NoTextLayer_ReturnsEmpty()
    {
        var path = P("blank.pdf");
        TestPdfFactory.CreateBlank(path, 1, 300, 400);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            using var tp = page.LoadText();
            Assert.Empty(tp.FindAll("PAGE", false, false));
        });
    }

    [Fact]
    public async Task GetCharIndexAtPos_OnGlyph_ReturnsIndex()
    {
        var path = P("lab.pdf");
        TestPdfFactory.CreateLabeled(path, [(300, 400)]);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            using var tp = page.LoadText();
            var r = tp.GetRects(0, 1)[0];
            double cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2; // Top=아래 y
            Assert.True(tp.GetCharIndexAtPos(cx, cy, 2) >= 0);
            Assert.Equal(-1, tp.GetCharIndexAtPos(10, 10, 2));
        });
    }
}
