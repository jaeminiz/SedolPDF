using Pdf.Native;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests;

public class SaveServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-save-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public SaveServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);
    private static SaveService Svc(params ISavePostProcessor[] pp) => new(pp);

    private async Task<int> PageCountOf(string path)
    {
        await using var s = await DocumentSession.OpenAsync(_t, path, null);
        return s.Pages.Length;
    }

    [Fact]
    public async Task Save_NewPath_WritesAndMarksSaved()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var target = P("new.pdf");
        await Svc().SaveAsync(s, target);

        Assert.True(File.Exists(target));
        Assert.Equal(1, await PageCountOf(target));
        Assert.False(s.IsDirty);
        Assert.Equal(target, s.FilePath);
    }

    [Fact]
    public async Task Save_Overwrite_ReplacesOriginal_NoTempLeft()
    {
        var p = P("a.pdf");
        TestPdfFactory.CreateBlank(p, 3, 595, 842);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.Commands.ExecuteAsync(new DeletePagesCommand([s.Pages[0].Id]));
        await Svc().SaveAsync(s, p);

        Assert.Equal(2, await PageCountOf(p));
        Assert.Empty(Directory.GetFiles(_dir, "~*.tmp"));
        Assert.False(s.IsDirty);
    }

    [Fact]
    public async Task Save_TargetLocked_OriginalIntactAndTempRemoved()
    {
        var p = P("locked.pdf");
        TestPdfFactory.CreateBlank(p, 3, 595, 842);
        var before = File.ReadAllBytes(p);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.Commands.ExecuteAsync(new DeletePagesCommand([s.Pages[0].Id]));

        SaveException ex;
        using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ex = await Assert.ThrowsAsync<SaveException>(() => Svc().SaveAsync(s, p));
        }
        Assert.Equal(SaveStage.Replace, ex.Stage);
        Assert.Equal(before, File.ReadAllBytes(p));
        Assert.Empty(Directory.GetFiles(_dir, "~*.tmp"));
        Assert.True(s.IsDirty);
    }

    private sealed class ThrowingPost : ISavePostProcessor
    {
        public bool AppliesTo(DocumentSession s) => true;
        public void Process(string tempPath, string? password) => throw new InvalidOperationException("boom");
    }

    private sealed class OverwritePost(string src) : ISavePostProcessor
    {
        public bool AppliesTo(DocumentSession s) => true;
        public void Process(string tempPath, string? password) => File.Copy(src, tempPath, true);
    }

    private sealed class NeverPost : ISavePostProcessor
    {
        public bool Called;
        public bool AppliesTo(DocumentSession s) => false;
        public void Process(string tempPath, string? password) => Called = true;
    }

    [Fact]
    public async Task Save_PostProcessorThrows_StagePostProcess_OriginalIntact()
    {
        var p = P("pp.pdf");
        TestPdfFactory.CreateBlank(p, 2, 595, 842);
        var before = File.ReadAllBytes(p);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        var ex = await Assert.ThrowsAsync<SaveException>(() => Svc(new ThrowingPost()).SaveAsync(s, p));
        Assert.Equal(SaveStage.PostProcess, ex.Stage);
        Assert.Equal(before, File.ReadAllBytes(p));
        Assert.Empty(Directory.GetFiles(_dir, "~*.tmp"));
    }

    [Fact]
    public async Task Save_PostProcessorNotApplicable_NotCalled()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var n = new NeverPost();
        await Svc(n).SaveAsync(s, P("n.pdf"));
        Assert.False(n.Called);
    }

    [Fact]
    public async Task Save_VerifyMismatch_StageVerify()
    {
        var one = P("one.pdf");
        TestPdfFactory.CreateBlank(one, 1, 595, 842);
        var p = P("v.pdf");
        TestPdfFactory.CreateBlank(p, 2, 595, 842);
        var before = File.ReadAllBytes(p);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        var ex = await Assert.ThrowsAsync<SaveException>(() => Svc(new OverwritePost(one)).SaveAsync(s, p));
        Assert.Equal(SaveStage.Verify, ex.Stage);
        Assert.Equal(before, File.ReadAllBytes(p));
        Assert.Empty(Directory.GetFiles(_dir, "~*.tmp"));
    }

    [Fact]
    public async Task Save_StaleTempExists_Overwritten()
    {
        var p = P("a.pdf");
        File.WriteAllText(P("~a.pdf.tmp"), "stale garbage");
        await using var s = await DocumentSession.CreateNewAsync(_t);
        await Svc().SaveAsync(s, p);
        Assert.Equal(1, await PageCountOf(p));
        Assert.Empty(Directory.GetFiles(_dir, "~*.tmp"));
    }

    [Fact]
    public async Task Save_KoreanPath()
    {
        Directory.CreateDirectory(P("한글 폴더"));
        var p = P("한글 폴더/도면 1.pdf");
        await using var s = await DocumentSession.CreateNewAsync(_t);
        await Svc().SaveAsync(s, p);
        Assert.Equal(1, await PageCountOf(p));
    }
}
