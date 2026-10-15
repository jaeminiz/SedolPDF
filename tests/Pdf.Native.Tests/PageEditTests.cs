using System.Drawing;
using PdfEditor.TestFixtures;

namespace Pdf.Native.Tests;

public class PageEditTests : IDisposable
{
    private const float A4W = 595.28f, A4H = 841.89f;
    private const float A5W = 419.53f, A5H = 595.28f;
    private const float LtW = 612f, LtH = 792f;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-pe-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public PageEditTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static float[] Widths(PdfDocument doc)
        => Enumerable.Range(0, doc.PageCount).Select(i => (float)Math.Round(doc.GetPageSizePt(i).Width, 1)).ToArray();

    private static float[] Round(params float[] v) => v.Select(x => (float)Math.Round(x, 1)).ToArray();

    // 폭이 모두 다른 5쪽: p0..p4
    private static readonly (float W, float H)[] Five =
        [(300, 400), (310, 400), (320, 400), (330, 400), (340, 400)];

    [Fact]
    public async Task DeletePage_RemovesThatPage()
    {
        var path = P("three.pdf");
        TestPdfFactory.CreateLabeled(path, [(A4W, A4H), (A5W, A5H), (LtW, LtH)]);
        var w = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            doc.DeletePage(1);
            Assert.Equal(2, doc.PageCount);
            return Widths(doc);
        });
        Assert.Equal(Round(A4W, LtW), w);
    }

    [Fact]
    public async Task MovePages_FollowsPdfiumSemantics()
    {
        var path = P("five.pdf");
        TestPdfFactory.CreateLabeled(path, Five);
        var (first, second) = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            doc.MovePages([3, 4], 0);
            var a = Widths(doc);
            doc.MovePages([0], 4);
            return (a, Widths(doc));
        });
        Assert.Equal(Round(330, 340, 300, 310, 320), first);
        // [p3,p4,p0,p1,p2]에서 0번(p3)을 마지막으로 → [p4,p0,p1,p2,p3]
        Assert.Equal(Round(340, 300, 310, 320, 330), second);
    }

    [Fact]
    public async Task ImportPages_FromOtherDocument_AtIndex()
    {
        var pa = P("a.pdf");
        var pb = P("b.pdf");
        TestPdfFactory.CreateLabeled(pa, [(300, 400), (310, 400)]);
        TestPdfFactory.CreateLabeled(pb, [(400, 500), (410, 500), (420, 500)]);
        var w = await _t.InvokeAsync(() =>
        {
            using var a = PdfDocument.Open(_t, pa);
            using var b = PdfDocument.Open(_t, pb);
            a.ImportPages(b, [2, 0], 1);
            return Widths(a);
        });
        Assert.Equal(Round(300, 420, 400, 310), w);
    }

    [Fact]
    public async Task ImportPages_KeepsAnnotations()
    {
        var pa = P("src.pdf");
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.CreateNew(_t);
            doc.AddPage(595, 842);
            AnnotTestHelper.AddSquareAndSquiggly(doc, 0);
            doc.SaveCopy(pa);
        });
        var count = await _t.InvokeAsync(() =>
        {
            using var src = PdfDocument.Open(_t, pa);
            using var dest = PdfDocument.CreateNew(_t);
            dest.ImportPages(src, [0], 0);
            using var page = dest.LoadPage(0);
            return page.AnnotationCount;
        });
        // 주석 2개(Square, Squiggly)를 만들었으므로 둘 다 유지돼야 한다.
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task InsertBlankPage_AtIndex_WithSize()
    {
        var path = P("one.pdf");
        TestPdfFactory.CreateBlank(path, 1, 595, 842);
        var (count, size, second) = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            doc.InsertBlankPage(0, 300, 400);
            return (doc.PageCount, doc.GetPageSizePt(0), doc.GetPageSizePt(1));
        });
        Assert.Equal(2, count);
        Assert.Equal(300, size.Width, 0.01);
        Assert.Equal(400, size.Height, 0.01);
        Assert.Equal(595, second.Width, 0.5);
    }

    [Fact]
    public async Task SetRotation_SwapsReportedSize()
    {
        var path = P("a4.pdf");
        var saved = P("a4r.pdf");
        TestPdfFactory.CreateBlank(path, 1, A4W, A4H);
        var (pageSize, byIndex) = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            page.Rotation = 1;
            var r = (page.SizePt, doc.GetPageSizePt(0));
            doc.SaveCopy(saved);
            return r;
        });
        Assert.Equal(841.89, pageSize.Width, 0.05);
        Assert.Equal(595.28, pageSize.Height, 0.05);
        Assert.Equal(841.89, byIndex.Width, 0.05);
        Assert.Equal(595.28, byIndex.Height, 0.05);

        var rot = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, saved);
            using var page = doc.LoadPage(0);
            return page.Rotation;
        });
        Assert.Equal(1, rot);
    }

    private static bool IsBlack(byte[] bgra, int w, int x, int y)
    {
        int o = (y * w + x) * 4;
        return bgra[o] < 60 && bgra[o + 1] < 60 && bgra[o + 2] < 60;
    }

    private static bool IsWhite(byte[] bgra, int w, int x, int y)
    {
        int o = (y * w + x) * 4;
        return bgra[o] > 200 && bgra[o + 1] > 200 && bgra[o + 2] > 200;
    }

    [Fact]
    public async Task RotatedPage_RenderTile_TopLeftMarkerMovesToTopRight()
    {
        var path = P("marker.pdf");
        TestPdfFactory.CreateLabeled(path, [(A4W, A4H)]);
        var (bmp, w) = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            page.Rotation = 1;
            int pw = (int)Math.Round(page.SizePt.Width), ph = (int)Math.Round(page.SizePt.Height);
            return (page.RenderTile(1.0, 0, 0, pw, ph, false), pw);
        });
        // 오른쪽 위 20x20 안쪽(가장자리 2px 여유)은 검정, 왼쪽 위는 흰색
        for (int y = 2; y < 18; y += 4)
            for (int x = w - 18; x < w - 2; x += 4)
                Assert.True(IsBlack(bmp, w, x, y), $"오른쪽 위 ({x},{y})");
        for (int y = 2; y < 18; y += 4)
            for (int x = 2; x < 18; x += 4)
                Assert.True(IsWhite(bmp, w, x, y), $"왼쪽 위 ({x},{y})");
    }

    [Fact]
    public async Task BoundsPt_ReflectsCropBox()
    {
        var path = P("crop.pdf");
        TestPdfFactory.CreateWithCropBox(path, (100, 100, 400, 500));
        var b = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            return page.BoundsPt;
        });
        var expected = RectangleF.FromLTRB(100, 100, 400, 500);
        Assert.Equal(expected.Left, b.Left, 0.5);
        Assert.Equal(expected.Top, b.Top, 0.5);
        Assert.Equal(expected.Right, b.Right, 0.5);
        Assert.Equal(expected.Bottom, b.Bottom, 0.5);
    }

    [Fact]
    public async Task OpenBytes_Works()
    {
        var path = P("bytes.pdf");
        TestPdfFactory.CreateBlank(path, 2, 595, 842);
        var data = File.ReadAllBytes(path);
        var n = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.OpenBytes(_t, data);
            return doc.PageCount;
        });
        Assert.Equal(2, n);
    }
}
