using Pdf.Native;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.Document.Output;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests.Output;

/// <summary>0.15.0 작업 5: 그러데이션 쪽에서 원본 도형의 선·채움·굵기와 원본 글자 색을 바꿔도 묻지 않고 저장된다(저장 모양 = 화면 모양, 다시 열어도 유지).</summary>
public sealed class GradientStyleSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-gradstyle-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public GradientStyleSaveTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { _t.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private string P(string n) => Path.Combine(_dir, n);
    private static OutputCopyBuilder Builder(DocumentSession s) => new(null, new PdfSharpAnnotationPatcher(() => []), s.Assets);

    private async Task<DocumentSession> Open()
    {
        string p = P(Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateGradientWithTrickyText(p);
        return await DocumentSession.OpenAsync(_t, p, null);
    }

    private Task<List<PageObjectEntry>> Entries(DocumentSession s) => _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.ToList());

    private byte[] Render(string path) => _t.InvokeAsync(() =>
    {
        using var d = PdfDocument.Open(_t, path, null);
        using var p = d.LoadPage(0);
        return p.RenderTile(1.0, 0, 0, 595, 842, false);
    }).GetAwaiter().GetResult();

    private static (byte R, byte G, byte B) Px(byte[] bgra, double pageX, double pageY)
    {
        int x = (int)Math.Round(pageX), y = (int)Math.Round(842 - pageY);
        int i = (y * 595 + x) * 4;
        return (bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    private static int DiffOutside(byte[] a, byte[] b, params PdfRectF[] skips)
    {
        int n = 0;
        for (int y = 0; y < 842; y++)
            for (int x = 0; x < 595; x++)
            {
                if (skips.Any(r => x >= r.Left - 6 && x <= r.Right + 6 && 842 - y >= r.Bottom - 6 && 842 - y <= r.Top + 6)) continue;
                int i = (y * 595 + x) * 4;
                if (Math.Abs(a[i] - b[i]) > 16 || Math.Abs(a[i + 1] - b[i + 1]) > 16 || Math.Abs(a[i + 2] - b[i + 2]) > 16) n++;
            }
        return n;
    }

    private static bool Near((byte R, byte G, byte B) c, int r, int g, int b, int tol = 40) => Math.Abs(c.R - r) <= tol && Math.Abs(c.G - g) <= tol && Math.Abs(c.B - b) <= tol;

    /// <summary>빨간 테두리만 있는 사각형(80x50, 선 2pt)을 고른다.</summary>
    private static PageObjectEntry RedRect(List<PageObjectEntry> all)
        => all.First(e => e.Type == PdfPageObjectType.Path && Math.Abs(e.Bounds.Width - 80) < 6 && Math.Abs(e.Bounds.Height - 50) < 6 && e.Bounds.Left < 150);

    [Fact]
    public async Task OriginalPathStyle_OnGradientPage_Saves_AndSurvivesReopen()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var rect = RedRect(await Entries(s));
        var style = new ShapeStyle(new PdfColor(0, 128, 0), 4f, new PdfColor(255, 255, 0));   // 초록 선 4pt + 노란 채움(원래는 빨간 선만)
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new SetShapeStyleEdit(s.Content.NewId(), rect.Ref, style)]));
        string outPath = P("out.pdf");
        await Builder(s).BuildAsync(s, outPath);   // 0.14.0: ContentRegenerationException("needs the PDFium path")

        var after = Render(outPath);
        var b = rect.Bounds;
        Assert.True(Near(Px(after, (b.Left + b.Right) / 2, (b.Bottom + b.Top) / 2), 255, 255, 0), "채움은 노란색이어야 한다");
        Assert.True(Near(Px(after, b.Left + 1, (b.Bottom + b.Top) / 2), 0, 128, 0, 70), "선은 초록이어야 한다");
        Assert.Equal(0, DiffOutside(Render(s.FilePath!), after, b));
        var (stroke, fill, width) = await _t.InvokeAsync(() =>
        {
            using var d = PdfDocument.Open(_t, outPath, null);
            using var p = d.LoadPage(0);
            var o = p.GetObject(rect.Ref.OriginalIndex);
            return (o.StrokeColor, o.FillColor, o.StrokeWidth);
        });
        Assert.Equal(new PdfRgba(0, 128, 0, 255), stroke);
        Assert.Equal(new PdfRgba(255, 255, 0, 255), fill);
        Assert.InRange(width, 3.9f, 4.1f);
    }

    [Fact]
    public async Task OriginalPath_FillOnlyAndNoStroke_Saves()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var rect = RedRect(await Entries(s));
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new SetShapeStyleEdit(s.Content.NewId(), rect.Ref, new ShapeStyle(null, 1f, new PdfColor(0, 0, 255)))]));   // 파란 채움만
        string outPath = P("out.pdf");
        await Builder(s).BuildAsync(s, outPath);
        var after = Render(outPath);
        var b = rect.Bounds;
        Assert.True(Near(Px(after, (b.Left + b.Right) / 2, (b.Bottom + b.Top) / 2), 0, 0, 255));
        Assert.False(Near(Px(after, b.Left + 1, (b.Bottom + b.Top) / 2), 255, 0, 0, 60), "빨간 테두리는 사라져야 한다");
        Assert.Equal(0, DiffOutside(Render(s.FilePath!), after, b));
    }

    [Fact]
    public async Task OriginalTextColor_OnGradientPage_Saves_AndNewColorShows()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var all = await Entries(s);
        // 보통 글 줄(이어 쓰기 아닌 것: 기준선 y=842-160 근처)을 빨갛게
        var text = all.Where(e => e.Type == PdfPageObjectType.Text).First(e => e.Bounds.Bottom > 842 - 175 && e.Bounds.Bottom < 842 - 150);
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new SetTextColorEdit(s.Content.NewId(), text.Ref, new PdfColor(255, 0, 0))]));
        string outPath = P("out.pdf");
        await Builder(s).BuildAsync(s, outPath);   // 0.14.0: ContentRegenerationException("unsupported edit")

        var after = Render(outPath);
        int red = 0, dark = 0;
        for (int y = 0; y < 842; y++)
            for (int x = 0; x < 595; x++)
            {
                if (x < text.Bounds.Left || x > text.Bounds.Right || 842 - y < text.Bounds.Bottom || 842 - y > text.Bounds.Top) continue;
                int i = (y * 595 + x) * 4;
                if (after[i + 2] > 200 && after[i + 1] < 90 && after[i] < 90) red++;
                else if (after[i + 2] < 90 && after[i + 1] < 90 && after[i] < 90) dark++;
            }
        Assert.True(red >= 15, $"red pixels {red}");
        Assert.Equal(0, dark);
        Assert.Equal(0, DiffOutside(Render(s.FilePath!), after, text.Bounds));
    }

    [Fact]
    public async Task MoveAndStyle_OnOneObject_BothApplied()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var rect = RedRect(await Entries(s));
        await s.Commands.ExecuteAsync(new ContentEditCommand(id,
        [
            new TransformEdit(s.Content.NewId(), rect.Ref, PdfMatrix.Translate(200, 0)),
            new SetShapeStyleEdit(s.Content.NewId(), rect.Ref, new ShapeStyle(new PdfColor(0, 0, 255), 3f, new PdfColor(255, 255, 0))),
        ]));
        string outPath = P("out.pdf");
        await Builder(s).BuildAsync(s, outPath);
        var after = Render(outPath);
        var b = rect.Bounds;
        double cy = (b.Bottom + b.Top) / 2;
        Assert.True(Near(Px(after, (b.Left + b.Right) / 2 + 200, cy), 255, 255, 0), "옮긴 자리의 채움");
        Assert.True(Near(Px(after, (b.Left + b.Right) / 2, cy), 255, 255, 255, 30), "옛 자리는 비어 있어야 한다");
        var moved = new PdfRectF(b.Left + 200, b.Bottom, b.Right + 200, b.Top);
        Assert.Equal(0, DiffOutside(Render(s.FilePath!), after, b, moved));
    }
}
