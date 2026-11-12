using Pdf.Native;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.ContentStream;
using PdfEditor.Document;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Objects;

/// <summary>0.10.0 Task 8: 객체를 끄는 동안 보여 줄 객체 그림.</summary>
public sealed class ObjectDragImageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-dragimg-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public ObjectDragImageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Make()
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateFrameWithInner(p);
        return p;
    }

    private static List<PdfPageObject> Objects(PdfPage page) => Enumerable.Range(0, page.ObjectCount).Select(page.GetObject).ToList();

    private static PdfPageObject Green(PdfPage page) => Objects(page).First(o => o.Type == PdfPageObjectType.Path && o.FillColor is { G: > 100, R: < 50 });

    private static byte Alpha((byte[] Bgra, int W, int H) img, PdfRectF region, PdfPointF pt, double scale)
    {
        int x = (int)((pt.X - region.Left) * scale), y = (int)((region.Top - pt.Y) * scale);
        return img.Bgra[(y * img.W + x) * 4 + 3];
    }

    [Fact]
    public async Task RendersOnlyChosen()
    {
        string p = Make();
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var green = Green(page);
            var region = new PdfRectF(0, 0, page.SizePt.Width, page.SizePt.Height);
            var img = ObjectDragImage.Render(page, [green], region, 1.0)!.Value;
            var gb = green.Bounds;
            Assert.Equal(255, Alpha(img, region, new((gb.Left + gb.Right) / 2, (gb.Bottom + gb.Top) / 2), 1.0));
            // 파란 사각형이 있던 자리(선택하지 않은 객체)는 투명
            var blue = Objects(page).First(o => o.Type == PdfPageObjectType.Path && o.StrokeColor is { B: > 200, R: < 50 });
            var bb = blue.Bounds;
            Assert.Equal(0, Alpha(img, region, new(bb.Left + 1, (bb.Bottom + bb.Top) / 2), 1.0));
        });
    }

    [Fact]
    public async Task IsActiveRestored()
    {
        string p = Make();
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            Objects(page)[0].IsActive = false;   // 처음부터 꺼 둔 객체도 그대로 꺼져 있어야 한다
            var before = Objects(page).Select(o => o.IsActive).ToArray();
            ObjectDragImage.Render(page, [Green(page)], new PdfRectF(0, 0, 595, 842), 0.5);
            Assert.Equal(before, Objects(page).Select(o => o.IsActive).ToArray());
        });
    }

    [Fact]
    public async Task RenderThrows_IsActiveRestored()
    {
        string p = Make();
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var before = Objects(page).Select(o => o.IsActive).ToArray();
            Assert.Throws<InvalidOperationException>(() =>
                ObjectDragImage.Render(page, [Green(page)], new PdfRectF(0, 0, 595, 842), 1.0, (_, _, _) => throw new InvalidOperationException("boom")));
            Assert.Equal(before, Objects(page).Select(o => o.IsActive).ToArray());
        });
    }

    [Fact]
    public async Task LargeSelection_IsCapped()
    {
        string p = Make();
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var img = ObjectDragImage.Render(page, [Green(page)], new PdfRectF(0, 0, 5000, 5000), 1.0)!.Value;
            Assert.Equal(ObjectDragImage.MaxSidePx, Math.Max(img.W, img.H));
        });
    }

    [Fact]
    public async Task NothingChosen_OrEmptyBounds_ReturnsNull()
    {
        string p = Make();
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            Assert.Null(ObjectDragImage.Render(page, [], new PdfRectF(0, 0, 100, 100), 1.0));
            Assert.Null(ObjectDragImage.Render(page, [Green(page)], new PdfRectF(10, 10, 10, 50), 1.0));
        });
    }

    [Fact]
    public async Task SaveAfterPreview_ContentUnchanged()
    {
        string src = Make();
        string a = Path.Combine(_dir, "a.pdf"), b = Path.Combine(_dir, "b.pdf");
        await using (var s = await DocumentSession.OpenAsync(_t, src, null))
            await new SaveService([]).SaveAsync(s, a);
        await using (var s = await DocumentSession.OpenAsync(_t, src, null))
        {
            await s.Thread.InvokeAsync(() =>
            {
                var page = s.GetLoadedPage(s.Pages[0].Id);
                ObjectDragImage.Render(page, [Green(page)], new PdfRectF(0, 0, 595, 842), 1.0);
                return 0;
            });
            await new SaveService([]).SaveAsync(s, b);
        }
        Assert.Equal(Content(a), Content(b));
    }

    private static byte[] Content(string path)
    {
        using var d = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        return PageContent.Load(d.Pages[0]).Data!;
    }
}
