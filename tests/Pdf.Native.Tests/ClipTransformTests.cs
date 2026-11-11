using PdfEditor.TestFixtures;

namespace Pdf.Native.Tests;

/// <summary>
/// 0.10.0 Task 0: 객체를 옮기거나 돌릴 때 객체가 들고 있는 잘림 경로도 함께 따라가야 한다.
/// 시험 PDF의 잘림 상자(70x20)는 글 앞부분만 보이게 자른다. 같은 모양 그대로 옮기거나 돌렸다면 잉크 수가 그대로여야 한다
/// (PDFium은 객체 전체를 감싸는 잘림은 그리기에서 무시하므로, 실제로 자르는 상자로 시험한다).
/// </summary>
public sealed class ClipTransformTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-cliptr-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public ClipTransformTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static List<PdfPageObject> TextObjects(PdfPage page) =>
        Enumerable.Range(0, page.ObjectCount).Select(page.GetObject).Where(o => o.Type == PdfPageObjectType.Text).ToList();

    /// <summary>객체 하나만 켜고 그린 그림에서 잉크(알파 &gt; 0) 픽셀 수.</summary>
    private static int Ink(PdfPage page, PdfPageObject only, PdfRectF region)
    {
        for (int i = 0; i < page.ObjectCount; i++) { var o = page.GetObject(i); o.IsActive = o.Handle == only.Handle; }
        var (bgra, _, _) = page.RenderRegionTransparent(region, 2.0);
        int n = 0;
        for (int i = 3; i < bgra.Length; i += 4) if (bgra[i] > 0) n++;
        return n;
    }

    private static PdfMatrix RotateAbout(float deg, float cx, float cy)
    {
        double r = deg * Math.PI / 180, cs = Math.Cos(r), sn = Math.Sin(r);
        return new PdfMatrix((float)cs, (float)sn, (float)-sn, (float)cs,
            (float)(cx - cs * cx + sn * cy), (float)(cy - sn * cx - cs * cy));
    }

    private static readonly PdfRectF Around = new(60, 50, 260, 170);

    [Fact]
    public async Task TransformBy_RotatesClipToo()
    {
        string p = Path.Combine(_dir, "c.pdf"); ClippedTextPdfFactory.Create(p);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var clipped = TextObjects(page).Single(o => o.Bounds.Bottom < 200);
            int before = Ink(page, clipped, Around);
            Assert.InRange(before, 100, 2000);   // 잘려서 글 일부만 있다
            clipped.TransformBy(RotateAbout(30, 135, 110));
            int after = Ink(page, clipped, Around);
            Assert.InRange(after, (int)(before * 0.9), (int)(before * 1.1) + 1);
        });
    }

    [Fact]
    public async Task TransformBy_MoveFar_DoesNotVanish()
    {
        string p = Path.Combine(_dir, "m.pdf"); ClippedTextPdfFactory.Create(p);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var clipped = TextObjects(page).Single(o => o.Bounds.Bottom < 200);
            int before = Ink(page, clipped, Around);
            clipped.TransformBy(PdfMatrix.Translate(200, 0));
            int after = Ink(page, clipped, new PdfRectF(Around.Left + 200, Around.Bottom, Around.Right + 200, Around.Top));
            Assert.True(before > 100);
            Assert.InRange(after, (int)(before * 0.95), (int)(before * 1.05) + 1);
        });
    }

    [Fact]
    public async Task TransformBy_NoClip_StillWorks()
    {
        string p = Path.Combine(_dir, "n.pdf"); ClippedTextPdfFactory.Create(p);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, p);
            using var page = doc.LoadPage(0);
            var twin = TextObjects(page).Single(o => o.Bounds.Bottom > 300);
            float x0 = twin.Bounds.Left;
            twin.TransformBy(PdfMatrix.Translate(50, 0));
            Assert.Equal(x0 + 50, twin.Bounds.Left, 0.5f);
        });
    }
}
