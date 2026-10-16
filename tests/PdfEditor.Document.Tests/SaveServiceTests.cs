using Pdf.Native;
using PdfEditor.Document.Annotations;
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

    // --- M2 Task 8: 저장 직전 주석 Flush ---

    private static Annotation NewSquare(DocumentSession s, float x = 100, float y = 100) => new()
    {
        Id = s.Annotations.NewId(),
        Kind = AnnotKind.Square,
        Rect = new PdfRectF(x, y, x + 80, y + 60),
        Color = new PdfColor(229, 55, 45),
        Opacity = 1f,
        BorderWidth = 2f,
        Name = Guid.NewGuid().ToString("D"),
        Flags = PdfAnnotFlags.Print,
    };

    private static Annotation NewInk(DocumentSession s) => new()
    {
        Id = s.Annotations.NewId(),
        Kind = AnnotKind.Ink,
        Rect = new PdfRectF(50, 50, 150, 120),
        Color = new PdfColor(229, 55, 45),
        BorderWidth = 2f,
        InkStrokes = [[new PdfPointF(60, 60), new PdfPointF(100, 110), new PdfPointF(140, 70)]],
        Name = Guid.NewGuid().ToString("D"),
        Flags = PdfAnnotFlags.Print,
    };

    private static List<(string Subtype, string? Nm, int Flags)> RawAnnots(string path)
    {
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var list = new List<(string, string?, int)>();
        var arr = doc.Pages[0].Elements.GetArray("/Annots");
        if (arr is null) return list;
        foreach (var item in arr.Elements)
        {
            var d = item is PdfSharp.Pdf.Advanced.PdfReference r ? (PdfSharp.Pdf.PdfDictionary)r.Value : (PdfSharp.Pdf.PdfDictionary)item;
            list.Add((d.Elements.GetName("/Subtype").TrimStart('/'),
                d.Elements.GetString("/NM") is { Length: > 0 } nm ? nm : null,
                d.Elements.GetInteger("/F")));
        }
        return list;
    }

    private static SaveService SvcA() => new([], new PdfEditor.Appearance.PdfAppearanceSource());

    [Fact]
    public async Task Save_NewInk_ReopenHasItWithPrintFlag()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var ink = NewInk(s);
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, ink));
        var target = P("ink.pdf");
        await SvcA().SaveAsync(s, target);

        var a = Assert.Single(RawAnnots(target));
        Assert.Equal("Ink", a.Subtype);
        Assert.Equal(ink.Name, a.Nm);
        Assert.NotEqual(0, a.Flags & PdfAnnotFlags.Print);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public async Task Save_EncryptedUserPassword_AnnotationSaved_StillEncrypted()
    {
        var p = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(p, 1, 595, 842, "secret");
        var outp = P("out.pdf");
        await using (var s = await DocumentSession.OpenAsync(_t, p, "secret"))
        {
            await s.AnnotationsReady;
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, NewSquare(s)));
            await SvcA().SaveAsync(s, outp);
        }
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => DocumentSession.OpenAsync(_t, outp, null));
        await using var again = await DocumentSession.OpenAsync(_t, outp, "secret");
        Assert.True(again.IsEncrypted);
        await again.AnnotationsReady;
        int count = await again.Thread.InvokeAsync(() => again.GetLoadedPage(again.Pages[0].Id).AnnotationCount);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SaveUndoSave_NoDuplicateOrOrphan()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var sq = NewSquare(s);
        var svc = SvcA();
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, sq));

        var f1 = P("s1.pdf");
        await svc.SaveAsync(s, f1);
        Assert.Equal([sq.Name], RawAnnots(f1).Select(a => a.Nm));

        await s.Commands.UndoAsync();
        var f2 = P("s2.pdf");
        await svc.SaveAsync(s, f2);
        Assert.Empty(RawAnnots(f2));

        await s.Commands.RedoAsync();
        var f3 = P("s3.pdf");
        await svc.SaveAsync(s, f3);
        Assert.Equal([sq.Name], RawAnnots(f3).Select(a => a.Nm));

        var sq2 = NewSquare(s, 200, 200);
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, sq2));
        var f4 = P("s4.pdf");
        await svc.SaveAsync(s, f4);
        Assert.Equal(2, RawAnnots(f4).Count);
        await s.Commands.UndoAsync();
        var f5 = P("s5.pdf");
        await svc.SaveAsync(s, f5);
        Assert.Equal([sq.Name], RawAnnots(f5).Select(a => a.Nm));
    }

    [Fact]
    public async Task Save_NoAppearanceSourceButChanges_ThrowsStageWrite_OriginalIntact()
    {
        var p = P("a.pdf");
        TestPdfFactory.CreateBlank(p, 1, 595, 842);
        var before = File.ReadAllBytes(p);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.AnnotationsReady;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, NewSquare(s)));

        var ex = await Assert.ThrowsAsync<SaveException>(() => Svc().SaveAsync(s, p));
        Assert.Equal(SaveStage.Write, ex.Stage);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal(before, File.ReadAllBytes(p));
        Assert.Empty(Directory.GetFiles(_dir, "~*.tmp"));
        Assert.True(s.IsDirty);
    }
}
