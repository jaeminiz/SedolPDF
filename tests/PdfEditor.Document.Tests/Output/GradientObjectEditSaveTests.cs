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
        var id = s.Pages[0].Id;
        var texts = (await Entries(s)).Where(e => e.Type == PdfPageObjectType.Text).ToList();
        Assert.True(texts.Count >= 3, "fixture should have the split line (2 objects) and the plain line");
        int originalCount = await _t.InvokeAsync(() => s.GetLoadedPage(id).ObjectCount);
        var shift = PdfMatrix.Translate(0, -200);
        await s.Commands.ExecuteAsync(new ContentEditCommand(id,
            [.. texts.Select(e => (ContentEdit)new DuplicateObjectEdit(s.Content.NewId(), id, e.Ref.OriginalIndex, shift))]));
        string outPath = P("out.pdf");
        await Builder(s).BuildAsync(s, outPath);   // 0.14.0: ContentRegenerationException("text not copyable")

        var copyBoxes = texts.Select(e => new PdfRectF(e.Bounds.Left, e.Bounds.Bottom - 200, e.Bounds.Right, e.Bounds.Top - 200)).ToArray();
        var after = await _t.InvokeAsync(() =>
        {
            using var d = PdfDocument.Open(_t, outPath, null);
            using var p = d.LoadPage(0);
            // BT head replay: earlier hidden shows may add extra text objects
            Assert.Equal(originalCount + texts.Count + 2, p.ObjectCount);   // 이어 쓴 둘째 Tj와 BT 안 cm 뒤 글만 앞 글의 안 보이는 사본이 하나씩 더 붙는다
            return Enumerable.Range(originalCount, p.ObjectCount - originalCount).Select(i => p.GetObject(i).Bounds).ToList();
        });
        foreach (var box in copyBoxes)
            Assert.Contains(after, b => Math.Abs(b.Left - box.Left) <= 0.5 && Math.Abs(b.Bottom - box.Bottom) <= 0.5 && Math.Abs(b.Right - box.Right) <= 0.5);
        Assert.Equal(0, DiffOutside(Render(s.FilePath!), Render(outPath), copyBoxes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveOrResizeTrickyText_Saves_AndOldPlaceIsBlank(bool resize)
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var texts = (await Entries(s)).Where(e => e.Type == PdfPageObjectType.Text).ToList();
        Assert.True(texts.Count >= 3);
        var moveBoxes = new List<PdfRectF>();
        var edits = new List<ContentEdit>();
        foreach (var e in texts)
        {
            var b = e.Bounds;
            // move: +250pt right. resize: 1.5x about bottom-left, also +250pt right.
            var m = resize ? PdfMatrix.ScaleAbout(1.5f, 1.5f, b.Left, b.Bottom).Then(PdfMatrix.Translate(250, 0)) : PdfMatrix.Translate(250, 0);
            moveBoxes.Add(resize ? new PdfRectF(b.Left + 250, b.Bottom, b.Left + 250 + b.Width * 1.5f, b.Bottom + b.Height * 1.5f)
                                 : new PdfRectF(b.Left + 250, b.Bottom, b.Right + 250, b.Top));
            edits.Add(new TransformEdit(s.Content.NewId(), e.Ref, m));
        }
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, edits));
        string outPath = P("out.pdf");
        await Builder(s).BuildAsync(s, outPath);   // 0.14.0: ContentRegenerationException("text not movable")

        var after = await _t.InvokeAsync(() =>
        {
            using var d = PdfDocument.Open(_t, outPath, null);
            using var p = d.LoadPage(0);
            return Enumerable.Range(0, p.ObjectCount).Select(i => p.GetObject(i)).Where(o => o.Type == PdfPageObjectType.Text).Select(o => o.Bounds).ToList();
        });
        foreach (var box in moveBoxes)
            Assert.Contains(after, b => Math.Abs(b.Left - box.Left) <= 0.6 && Math.Abs(b.Bottom - box.Bottom) <= 0.6 && Math.Abs(b.Right - box.Right) <= 1.5);
        var oldBoxes = texts.Select(e => e.Bounds).ToArray();
        var rOrig = Render(s.FilePath!); var rOut = Render(outPath);
        Assert.Equal(0, DiffOutside(rOrig, rOut, [.. oldBoxes, .. moveBoxes]));
        foreach (var o in oldBoxes) Assert.Equal(0, DarkPixels(rOut, o));   // old place shows no text
    }

    private static int DarkPixels(byte[] bgra, PdfRectF r)
    {
        int n = 0;
        for (int y = 0; y < 842; y++)
            for (int x = 0; x < 595; x++)
            {
                if (x < r.Left || x > r.Right || 842 - y < r.Bottom || 842 - y > r.Top) continue;
                int i = (y * 595 + x) * 4;
                if (bgra[i] < 200 || bgra[i + 1] < 200 || bgra[i + 2] < 200) n++;
            }
        return n;
    }
}