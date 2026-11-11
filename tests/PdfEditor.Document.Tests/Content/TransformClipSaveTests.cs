using Pdf.Native;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests.Content;

/// <summary>0.10.0 Task 0: 잘림 상자 안의 글을 돌려 저장해도 화면과 같아야 한다(잘림 경로도 함께 돌아감).</summary>
public sealed class TransformClipSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-clipsave-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public TransformClipSaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] Render(PdfPage page) =>
        page.RenderTile(1, 0, 0, (int)Math.Ceiling(page.SizePt.Width), (int)Math.Ceiling(page.SizePt.Height), false);

    private static int Different(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i += 4) if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 64) n++;
        return n;
    }

    [Fact]
    public async Task Save_RotatedClippedText_MatchesScreen()
    {
        string src = Path.Combine(_dir, "clipped.pdf"), dst = Path.Combine(_dir, "out.pdf");
        ClippedTextPdfFactory.Create(src);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        var id = s.Pages[0].Id;
        var (text, origin) = await _t.InvokeAsync(() =>
        {
            var e = s.GetObjectIndex(id).Entries.Where(x => x.Type == PdfPageObjectType.Text).OrderBy(x => x.Bounds.Bottom).First();
            return (e.Ref, e.Bounds);
        });
        float cx = 135, cy = 110;
        double r = 30 * Math.PI / 180, cs = Math.Cos(r), sn = Math.Sin(r);
        var rot = new PdfMatrix((float)cs, (float)sn, (float)-sn, (float)cs, (float)(cx - cs * cx + sn * cy), (float)(cy - sn * cx - cs * cy));
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new TransformEdit(s.Content.NewId(), text, rot)]));
        var screen = await _t.InvokeAsync(() => Render(s.GetLoadedPage(id)));
        await new SaveService([]).SaveAsync(s, dst);
        var saved = await _t.InvokeAsync(() => { using var d = PdfDocument.Open(_t, dst); using var p = d.LoadPage(0); return Render(p); });
        int diff = Different(screen, saved);
        Assert.True(diff <= Math.Max(2, screen.Length / 4 * 0.001), $"diff pixels {diff}");
        // 돌린 글이 실제로 보인다(잘려 사라지지 않았다)
        int ink = 0;
        for (int y = 842 - 170; y < 842 - 50; y++) for (int x = 60; x < 260; x++) { int i = (y * 595 + x) * 4; if (saved[i] < 128) ink++; }
        Assert.True(ink > 30, $"ink {ink}");
    }
}
