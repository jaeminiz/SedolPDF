using PdfEditor.TestFixtures;

namespace Pdf.Native.Tests;

/// <summary>0.9.0 Task 1: 개체 복사용 렌더 바인딩(그림 객체 비트맵, 영역 투명 렌더).</summary>
public sealed class DuplicateRenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-duprender-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public DuplicateRenderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task RenderImageBgra_ImageObject_ReturnsPixels()
    {
        string p = Path.Combine(_dir, "img.pdf");
        ImagePdfFactory.Create(p, new ImageSpec(40, 30, ImageKind.FlateRgb, 120, 90));
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var img = Enumerable.Range(0, page.ObjectCount).Select(page.GetObject).First(o => o.Type == PdfPageObjectType.Image);
            var r = img.RenderImageBgra(page);
            Assert.NotNull(r);
            Assert.True(r!.Value.W > 0 && r.Value.H > 0);
            Assert.Equal(r.Value.W * r.Value.H * 4, r.Value.Bgra.Length);
        });
    }

    [Fact]
    public async Task RenderImageBgra_PathObject_ReturnsNull()
    {
        string p = Path.Combine(_dir, "frame.pdf");
        ContentPdfFactory.CreateFrameWithInner(p);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var path = Enumerable.Range(0, page.ObjectCount).Select(page.GetObject).First(o => o.Type == PdfPageObjectType.Path);
            Assert.Null(path.RenderImageBgra(page));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RenderRegionTransparent_OnlyActiveObjectsDrawn_BackgroundTransparent(int rotation)
    {
        string p = Path.Combine(_dir, "frame" + rotation + ".pdf");
        ContentPdfFactory.CreateFrameWithInner(p);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            page.Rotation = rotation;
            // 칠한 초록 사각형만 켠다 (화면 위 기준 (350,400,100,100) → 쪽 좌표 y 342~442)
            PdfPageObject? green = null;
            for (int i = 0; i < page.ObjectCount; i++)
            {
                var o = page.GetObject(i);
                bool isGreen = o.Type == PdfPageObjectType.Path && o.FillColor is { G: > 100, R: < 50 };
                if (isGreen) green = o; else o.IsActive = false;
            }
            Assert.NotNull(green);
            var b = green!.Bounds.Inflate(4);
            var (bgra, w, h) = page.RenderRegionTransparent(b, 2.0);
            Assert.InRange(w, (int)Math.Ceiling(b.Width * 2) - 1, (int)Math.Ceiling(b.Width * 2) + 1);
            Assert.InRange(h, (int)Math.Ceiling(b.Height * 2) - 1, (int)Math.Ceiling(b.Height * 2) + 1);
            Assert.Equal(255, bgra[((h / 2) * w + w / 2) * 4 + 3]);
            Assert.Equal(0, bgra[3]);
            Assert.Equal(0, bgra[((h - 1) * w + (w - 1)) * 4 + 3]);
        });
    }
}
