using PdfEditor.TestFixtures;
using PdfSharp.Pdf;

namespace Pdf.Native.Tests;

public class BookmarkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public BookmarkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task GetBookmarks_Tree_TitlesPagesOpenState()
    {
        TestPdfFactory.CreateWithOutline(P("o.pdf"));
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("o.pdf"));
            var b = doc.GetBookmarks();
            Assert.Equal(2, b.Count);
            Assert.Equal("1장", b[0].Title);
            Assert.Equal(0, b[0].PageIndex);
            Assert.True(b[0].InitiallyOpen);
            Assert.Single(b[0].Children);
            Assert.Equal("1.1 절", b[0].Children[0].Title);
            Assert.Equal(1, b[0].Children[0].PageIndex);
            Assert.Equal("2장", b[1].Title);
            Assert.Equal(2, b[1].PageIndex);
            Assert.False(b[1].InitiallyOpen);
        });
    }

    [Fact]
    public async Task GetBookmarks_None_ReturnsEmpty()
    {
        TestPdfFactory.CreateBlank(P("a.pdf"), 1, 595, 842);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("a.pdf"));
            Assert.Empty(doc.GetBookmarks());
        });
    }

    [Fact]
    public async Task GetBookmarks_CyclicSibling_Terminates()
    {
        using (var d = new PdfSharp.Pdf.PdfDocument())
        {
            d.AddPage();
            var item = d.Outlines.Add("루프", d.Pages[0], true);
            // /Next가 자기 자신을 가리키도록 저수준으로 고친다
            item.Elements["/Next"] = item.Reference ?? throw new InvalidOperationException("no reference");
            d.Save(P("c.pdf"));
        }
        // 픽스처가 실제로 순환을 담고 있는지 확인 (저장된 파일에 /Next가 있어야 함)
        Assert.Contains("/Next", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(P("c.pdf"))));
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("c.pdf"));
            var b = doc.GetBookmarks();
            Assert.Single(b);
            Assert.Equal("루프", b[0].Title);
        });
    }
}
