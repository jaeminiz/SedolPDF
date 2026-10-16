using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;

namespace PdfEditor.Pages.Tests;

public class MergeServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-merge-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public MergeServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    [Fact]
    public async Task Merge_TwoFiles_PageOrderAndCount()
    {
        TestPdfFactory.CreateLabeled(P("a.pdf"), [(300, 400), (310, 410)]);
        TestPdfFactory.CreateLabeled(P("b.pdf"), [(500, 600), (510, 610), (520, 620)]);
        await using var s = await MergeService.CreateMergedAsync(_t,
            [new MergeSource(P("a.pdf"), null), new MergeSource(P("b.pdf"), null)], "merged", CancellationToken.None);
        Assert.Equal(5, s.Pages.Length);
        Assert.Equal([300f, 310f, 500f, 510f, 520f], s.Pages.Select(x => (float)Math.Round(x.WidthPt)).ToArray());
        Assert.Null(s.FilePath);
        Assert.True(s.IsDirty);
        Assert.Equal("merged", s.DisplayName);
        Assert.Equal(5, s.Pages.Select(x => x.Id).Distinct().Count());
        await _t.InvokeAsync(() => Assert.Equal(5, s.Document.PageCount));
    }

    [Fact]
    public async Task Merge_AnnotatedSources_OriginalsAttached()
    {
        TestPdfFactory.CreateBlank(P("p.pdf"), 1, 100, 100);
        AnnotatedPdfFactory.CreateMixed(P("a1.pdf"));
        AnnotatedPdfFactory.CreateMixed(P("a2.pdf"));
        await using var s = await MergeService.CreateMergedAsync(_t,
            [new MergeSource(P("p.pdf"), null), new MergeSource(P("a1.pdf"), null), new MergeSource(P("a2.pdf"), null)],
            "m", CancellationToken.None);
        Assert.Equal(3, s.Pages.Length);
        Assert.Empty(s.Annotations.Get(s.Pages[0].Id).Items);
        Assert.Equal(9, s.Annotations.Get(s.Pages[1].Id).Items.Length);
        Assert.Equal(9, s.Annotations.Get(s.Pages[2].Id).Items.Length);
        Assert.True(s.OriginalAnnotationsEditable);
    }

    [Fact]
    public async Task Merge_EncryptedSource_WithPassword()
    {
        TestPdfFactory.CreateEncrypted(P("e.pdf"), 2, 200, 300, "pw1");
        TestPdfFactory.CreateBlank(P("p.pdf"), 1, 100, 100);
        await using var s = await MergeService.CreateMergedAsync(_t,
            [new MergeSource(P("p.pdf"), null), new MergeSource(P("e.pdf"), "pw1")], "m", CancellationToken.None);
        Assert.Equal(3, s.Pages.Length);
        Assert.Equal(200f, s.Pages[1].WidthPt, 0.5f);
    }

    [Fact]
    public async Task Merge_WrongPassword_ThrowsPasswordRequired_NoSessionLeak()
    {
        TestPdfFactory.CreateEncrypted(P("e.pdf"), 1, 200, 300, "pw1");
        TestPdfFactory.CreateBlank(P("p.pdf"), 1, 100, 100);
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => MergeService.CreateMergedAsync(_t,
            [new MergeSource(P("p.pdf"), null), new MergeSource(P("e.pdf"), "bad")], "m", CancellationToken.None));
        // 스레드는 계속 정상 동작하고, 다음 병합도 된다
        await using var s = await MergeService.CreateMergedAsync(_t, [new MergeSource(P("p.pdf"), null)], "m", CancellationToken.None);
        Assert.Single(s.Pages);
    }

    [Fact]
    public async Task Merge_NoSources_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => MergeService.CreateMergedAsync(_t, [], "m", CancellationToken.None));
    }
}
