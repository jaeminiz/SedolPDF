using PdfEditor.TestFixtures;

namespace Pdf.Native.Tests;

/// <summary>M4 Task 2: 쪽 객체 래퍼(PdfPageObject·PdfPage 객체 메서드).</summary>
public sealed class PageObjectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-pageobj-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public PageObjectTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Doc()
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateDocumentLike(p);
        return p;
    }

    private static byte[] Render(PdfPage page) => page.RenderTile(0.5, 0, 0, (int)Math.Ceiling(page.SizePt.Width * 0.5), (int)Math.Ceiling(page.SizePt.Height * 0.5), false);

    private static int DiffPixels(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) > 8 || Math.Abs(a[i + 1] - b[i + 1]) > 8 || Math.Abs(a[i + 2] - b[i + 2]) > 8) n++;
        return n;
    }

    private static PdfPageObject First(PdfPage page, PdfPageObjectType type) =>
        Enumerable.Range(0, page.ObjectCount).Select(page.GetObject).First(o => o.Type == type);

    [Fact]
    public async Task Objects_EnumerateTypesAndBounds()
    {
        string p = Doc();
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var types = Enumerable.Range(0, page.ObjectCount).Select(i => page.GetObject(i).Type).ToList();
            Assert.Equal(3, types.Count(t => t == PdfPageObjectType.Text));
            Assert.Single(types, PdfPageObjectType.Image);
            Assert.Equal(2, types.Count(t => t == PdfPageObjectType.Path));
            var img = First(page, PdfPageObjectType.Image).Bounds;
            // 이미지: x 60~180, 위에서 200~290pt (쪽 높이 842 → y 552~642)
            Assert.Equal(60, img.Left, 1f); Assert.Equal(180, img.Right, 1f);
            Assert.Equal(552, img.Bottom, 1f); Assert.Equal(642, img.Top, 1f);
            var q = First(page, PdfPageObjectType.Image).RotatedBounds;
            Assert.True(q.P1.X < q.P2.X && q.P1.Y > q.P3.Y, "P1 = top-left, P2 = top-right, P3 = bottom-left");
        });
    }

    [Fact]
    public async Task SetIsActive_HidesInRender()
    {
        string p = Doc();
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var before = Render(page);
            var img = First(page, PdfPageObjectType.Image);
            img.IsActive = false;
            Assert.False(img.IsActive);
            Assert.True(DiffPixels(before, Render(page)) > 100);
            img.IsActive = true;
            Assert.Equal(0, DiffPixels(before, Render(page)));
        });
    }

    [Fact]
    public async Task Transform_MovesBounds()
    {
        string p = Doc();
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var img = First(page, PdfPageObjectType.Image);
            var b0 = img.Bounds;
            img.TransformBy(PdfMatrix.Translate(50, -20));
            var b1 = img.Bounds;
            Assert.Equal(b0.Left + 50, b1.Left, 0.5f);
            Assert.Equal(b0.Bottom - 20, b1.Bottom, 0.5f);
            img.TransformBy(PdfMatrix.ScaleAbout(2, 2, b1.Left, b1.Bottom));
            Assert.Equal(b1.Width * 2, img.Bounds.Width, 0.5f);
            Assert.Equal(b1.Left, img.Bounds.Left, 0.5f);
        });
    }

    [Fact]
    public async Task CreateTextLike_SameFontSizeMatrix()
    {
        string p = Doc();
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            PdfPageObject arial;
            using (var tp = page.LoadText())
                arial = Enumerable.Range(0, page.ObjectCount).Select(page.GetObject)
                    .First(o => o.Type == PdfPageObjectType.Text && (o.GetText(tp) ?? "").StartsWith("Arial"));
            var nt = page.CreateTextLike(arial, "Arial 321 CBA");
            Assert.NotNull(nt);
            Assert.False(nt!.IsAttached);
            Assert.Equal(arial.FontSize, nt.FontSize, 0.01f);
            Assert.Equal(arial.Matrix, nt.Matrix);
            Assert.Equal(arial.Font!.Value.BaseName, nt.Font!.Value.BaseName);
            page.InsertObject(nt);
            Assert.True(nt.IsAttached);
            using var tp2 = page.LoadText();
            Assert.Equal("Arial 321 CBA", nt.GetText(tp2));
        });
    }

    [Fact]
    public async Task CreateImage_RendersPixels()
    {
        string p = Doc();
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var bgra = new byte[10 * 10 * 4];
            for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 0; bgra[i + 1] = 0; bgra[i + 2] = 255; bgra[i + 3] = 255; }   // 빨강
            var img = page.CreateImage(bgra, 10, 10, new PdfMatrix(100, 0, 0, 100, 400, 50));   // 100×100pt, 왼쪽 아래 (400, 50)
            page.InsertObject(img);
            var px = page.RenderTile(1, 450, 842 - 100, 1, 1, false);   // (450, 100) 근처 = 이미지 안
            Assert.True(px[2] > 200 && px[1] < 60 && px[0] < 60, $"expected red, got {px[2]},{px[1]},{px[0]}");
        });
    }

    [Fact]
    public async Task CreatePath_StrokeFill()
    {
        string p = Doc();
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var path = page.CreatePath(400, 600);
            path.LineTo(500, 600); path.LineTo(500, 700); path.LineTo(400, 700); path.ClosePath();
            path.FillColor = new PdfRgba(0, 0, 255, 255);
            path.StrokeColor = new PdfRgba(0, 0, 0, 255);
            path.StrokeWidth = 2;
            path.SetDrawMode(fill: true, stroke: true);
            page.InsertObject(path);
            var px = page.RenderTile(1, 450, 842 - 650, 1, 1, false);
            Assert.True(px[0] > 200 && px[2] < 60, "expected blue fill");
            Assert.Equal(2, path.StrokeWidth, 0.01f);
        });
    }

    [Fact]
    public async Task InsertObject_AtIndex_Order()
    {
        string p = Doc();
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            int n = page.ObjectCount;
            var path = page.CreatePath(0, 0);
            path.LineTo(10, 10);
            page.InsertObject(path, 1);
            Assert.Equal(n + 1, page.ObjectCount);
            Assert.Equal(PdfPageObjectType.Path, page.GetObject(1).Type);
            Assert.Throws<InvalidOperationException>(() => page.InsertObject(path));
        });
    }

    [Fact]
    public async Task FormChildren_Enumerated()
    {
        string p = Path.Combine(_dir, "dwg.pdf");
        ContentPdfFactory.CreateDrawingLike(p);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var form = First(page, PdfPageObjectType.Form);
            var kids = form.FormChildren;
            Assert.Equal(3, kids.Count);
            Assert.Contains(kids, k => k.Type == PdfPageObjectType.Form);   // 안쪽 폼(2단)
            Assert.Empty(First(page, PdfPageObjectType.Path).FormChildren);
        });
    }

    [Fact]
    public async Task ObjectHandle_UsedAfterPageClosed_Throws()
    {
        string p = Doc();
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            PdfPageObject o;
            using (var page = doc.LoadPage(0)) o = page.GetObject(0);
            Assert.Throws<ObjectDisposedException>(() => _ = o.Bounds);
        });
    }

    [Fact]
    public async Task GenerateContent_OnCopy_KeepsEditsAndDropsInactive()
    {
        string p = Doc(), copy = Path.Combine(_dir, "copy.pdf");
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using (var page = doc.LoadPage(0))
            {
                First(page, PdfPageObjectType.Image).IsActive = false;
                page.GenerateContent();
            }
            doc.SaveCopy(copy);
            using var d2 = PdfDocument.Open(_t, copy);
            using var p2 = d2.LoadPage(0);
            Assert.DoesNotContain(Enumerable.Range(0, p2.ObjectCount), i => p2.GetObject(i).Type == PdfPageObjectType.Image);
        });
    }
}
