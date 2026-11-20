using Pdf.Native;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Content;
using PdfEditor.Document.Output;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests.Output;

/// <summary>0.15.0: 그러데이션 쪽에서 위치를 바로 알 수 없는 글(한 BT 안의 두 번째 Tj)도 복제·옮기기가 묻지 않고 저장된다.</summary>
public sealed class GradientObjectEditSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-gradsave-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public GradientObjectEditSaveTests() => Directory.CreateDirectory(_dir);
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

    private byte[] Render(string path)
        => _t.InvokeAsync(() =>
        {
            using var d = PdfDocument.Open(_t, path, null);
            using var p = d.LoadPage(0);
            return p.RenderTile(1.0, 0, 0, 595, 842, false);
        }).GetAwaiter().GetResult();

    /// <summary>skip 상자(쪽 좌표, +여유) 밖에서 다른 픽셀 수.</summary>
    private static int DiffOutside(byte[] a, byte[] b, params PdfRectF[] skips)
    {
        int n = 0;
        for (int y = 0; y < 842; y++)
            for (int x = 0; x < 595; x++)
            {
                if (skips.Any(r => x >= r.Left - 4 && x <= r.Right + 4 && 842 - y >= r.Bottom - 4 && 842 - y <= r.Top + 4)) continue;
                int i = (y * 595 + x) * 4;
                if (Math.Abs(a[i] - b[i]) > 16 || Math.Abs(a[i + 1] - b[i + 1]) > 16 || Math.Abs(a[i + 2] - b[i + 2]) > 16) n++;
            }
        return n;
    }

    [Fact]
    public async Task DuplicateTrickyText_Saves_WithoutLookQuestion()
    {
        await using var s = await Open();
        var all = await Entries(s);
        int origCount = all.Count;
        var texts = all.Where(e => e.Type == PdfPageObjectType.Text).ToList();
        Assert.True(texts.Count >= 3, "fixture should have the split line (2 objects) and the plain line");
        var shift = PdfMatrix.Translate(0, -200);
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id,
            [.. texts.Select(e => (ContentEdit)new DuplicateObjectEdit(s.Content.NewId(), s.Pages[0].Id, e.Ref.OriginalIndex, shift))]));
        string outPath = P("out.pdf");
        await Builder(s).BuildAsync(s, outPath);   // 지금은 ContentRegenerationException("text not copyable")
        int count = await _t.InvokeAsync(() => { using var d = PdfDocument.Open(_t, outPath, null); using var p = d.LoadPage(0); return p.ObjectCount; });
        Assert.Equal(origCount + texts.Count, count);
    }
}
