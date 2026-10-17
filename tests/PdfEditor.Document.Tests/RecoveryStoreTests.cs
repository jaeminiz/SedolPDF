using System.Security.Cryptography;
using Pdf.Native;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Recovery;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests;

public class RecoveryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-rec-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public RecoveryStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void OldRecoveryFolder_MigratedOnce_KeepsOldFiles()
    {
        string root = P("local");
        string oldDir = Path.Combine(root, "PdfEditor", "Recovery");
        Directory.CreateDirectory(oldDir);
        string id = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(Path.Combine(oldDir, id + ".pdf"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(oldDir, id + ".json"), "{}");
        string dir = RecoveryStore.DefaultFolderIn(root);
        Assert.Equal(Path.Combine(root, "SedolPDF", "Recovery"), dir);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(dir, id + ".pdf")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(dir, id + ".json")));
        Assert.True(File.Exists(Path.Combine(oldDir, id + ".pdf")));
        Assert.True(File.Exists(Path.Combine(oldDir, id + ".json")));
        // 두 번째 호출은 바뀐 새 파일을 덮어쓰지 않는다
        File.WriteAllText(Path.Combine(dir, id + ".json"), "changed");
        RecoveryStore.DefaultFolderIn(root);
        Assert.Equal("changed", File.ReadAllText(Path.Combine(dir, id + ".json")));
    }

    [Fact]
    public void OldRecoveryFolder_PartialFailure_RetriedLater()
    {
        string root = P("local2");
        string oldDir = Path.Combine(root, "PdfEditor", "Recovery");
        Directory.CreateDirectory(oldDir);
        string a = Path.Combine(oldDir, "a.pdf"), b = Path.Combine(oldDir, "b.pdf"), c = Path.Combine(oldDir, "c.pdf");
        foreach (var f in new[] { a, b, c }) File.WriteAllText(f, Path.GetFileName(f));
        string dir = Path.Combine(root, "SedolPDF", "Recovery");
        using (new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(dir, RecoveryStore.DefaultFolderIn(root)); // 예외 없음
            Assert.True(File.Exists(Path.Combine(dir, "a.pdf")));
            Assert.True(File.Exists(Path.Combine(dir, "c.pdf")));
            Assert.False(File.Exists(Path.Combine(dir, "b.pdf")));
        }
        RecoveryStore.DefaultFolderIn(root); // 표식이 없으므로 다시 시도
        Assert.Equal("b.pdf", File.ReadAllText(Path.Combine(dir, "b.pdf")));
        Assert.True(File.Exists(b));
    }
    private string P(string name) => Path.Combine(_dir, name);
    private RecoveryStore Store() => new(P("recovery"));

    private async Task<DocumentSession> OpenDirty(string name, int pages = 3)
    {
        string p = P(name);
        TestPdfFactory.CreateBlank(p, pages, 300, 400);
        var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.Commands.ExecuteAsync(new DeletePagesCommand([s.Pages[0].Id]));
        return s;
    }

    [Fact]
    public async Task Write_List_Delete_RoundTrip()
    {
        await using var s = await OpenDirty("a.pdf");
        var store = Store();
        await store.WriteAsync(s);

        var list = store.List();
        var e = Assert.Single(list);
        Assert.Equal(s.Id, e.SessionId);
        Assert.Equal(s.FilePath, e.OriginalPath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(P("a.pdf")))), e.OriginalSha256Hex);
        Assert.Equal("a.pdf", e.DisplayName);
        Assert.True(File.Exists(e.RecoveryPdfPath));
        Assert.Empty(Directory.GetFiles(P("recovery"), "*.tmp"));
        // 복구본에는 변경(2쪽)이 들어 있다
        await using (var r = await DocumentSession.OpenAsync(_t, e.RecoveryPdfPath, null))
            Assert.Equal(2, r.Pages.Length);

        await store.WriteAsync(s);   // 덮어쓰기도 항목은 하나
        Assert.Single(store.List());

        store.Delete(s.Id);
        Assert.Empty(store.List());
        Assert.Empty(Directory.GetFiles(P("recovery")));
        store.Delete(s.Id);          // 없는 항목 삭제는 조용히 넘어감
    }

    [Fact]
    public async Task List_SkipsCorruptJsonAndMissingPdf()
    {
        await using var s1 = await OpenDirty("a.pdf");
        await using var s2 = await OpenDirty("b.pdf");
        await using var s3 = await OpenDirty("c.pdf");
        var store = Store();
        await store.WriteAsync(s1);
        await store.WriteAsync(s2);
        await store.WriteAsync(s3);
        File.WriteAllText(Path.Combine(P("recovery"), s1.Id.ToString("N") + ".json"), "{not json");
        File.Delete(Path.Combine(P("recovery"), s2.Id.ToString("N") + ".pdf"));

        var list = store.List();
        Assert.Equal(s3.Id, Assert.Single(list).SessionId);
        Assert.Empty(new RecoveryStore(P("nowhere")).List());
    }

    [Fact]
    public async Task Write_DoesNotTouchOriginal()
    {
        await using var s = await OpenDirty("a.pdf");
        byte[] before = SHA256.HashData(File.ReadAllBytes(P("a.pdf")));
        var stamp = File.GetLastWriteTimeUtc(P("a.pdf"));
        await Store().WriteAsync(s);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(P("a.pdf"))));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(P("a.pdf")));
        Assert.True(s.IsDirty);
    }

    [Fact]
    public async Task Write_AfterSave_RecordsHashOfSavedFile()
    {
        await using var s = await OpenDirty("a.pdf");
        await new SaveService([]).SaveAsync(s, P("a.pdf"));
        await s.Commands.ExecuteAsync(new DeletePagesCommand([s.Pages[0].Id]));
        var store = Store();
        await store.WriteAsync(s);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(P("a.pdf")))), store.List()[0].OriginalSha256Hex);
    }

    [Fact]
    public async Task OpenRecovered_BindsAndIsDirty()
    {
        await using var s = await OpenDirty("a.pdf");
        var store = Store();
        await store.WriteAsync(s);
        var e = store.List()[0];

        await using var bound = await DocumentSession.OpenRecoveredAsync(_t, e, true, null);
        Assert.Equal(P("a.pdf"), bound.FilePath);
        Assert.Equal("a.pdf", bound.DisplayName);
        Assert.True(bound.IsDirty);
        Assert.Equal(2, bound.Pages.Length);

        await using var unbound = await DocumentSession.OpenRecoveredAsync(_t, e, false, null);
        Assert.Null(unbound.FilePath);
        Assert.True(unbound.IsDirty);
    }

    [Fact]
    public async Task Recovery_UnsavedAnnotation_InRecoveryPdf()
    {
        string p = P("r.pdf");
        TestPdfFactory.CreateBlank(p, 1, 300, 400);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.AnnotationsReady;
        var sq = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(50, 50, 150, 120),
            Color = new PdfColor(229, 55, 45), BorderWidth = 2f, Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print,
        };
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, sq));
        var store = new RecoveryStore(P("recovery"), new PdfEditor.Appearance.PdfAppearanceSource());
        await store.WriteAsync(s);
        Assert.True(s.IsDirty);

        var e = store.List()[0];
        await using var r = await DocumentSession.OpenRecoveredAsync(_t, e, true, null);
        await r.AnnotationsReady;
        var v = Assert.Single(r.Annotations.Get(r.Pages[0].Id).Items);
        Assert.Equal(AnnotKind.Square, v.Data.Kind);
        Assert.Equal(sq.Name, v.Data.Name);
        Assert.False(v.ModelDrawn);   // 복구본에서는 파일에 있던(원본) 주석으로 읽힌다
        Assert.Equal(1, await r.Thread.InvokeAsync(() => r.GetLoadedPage(r.Pages[0].Id).AnnotationCount));
    }

    private static PatchPlan PopupOnSquarePlan() => new(
        [new AnnotationPatch(0, 1, "", [new EnsurePopup(new PdfRectF(300, 600, 480, 720), false)])],
        [], []);

    private static int PopupCount(string path)
    {
        using var d = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var item = d.Pages[0].Elements["/Annots"];
        var arr = (PdfSharp.Pdf.PdfArray)((item as PdfSharp.Pdf.Advanced.PdfReference)?.Value ?? item)!;
        int n = 0;
        foreach (var e in arr.Elements)
        {
            var dict = (PdfSharp.Pdf.PdfDictionary)((e as PdfSharp.Pdf.Advanced.PdfReference)?.Value ?? e);
            if (dict.Elements.GetName("/Subtype") == "/Popup") n++;
        }
        return n;
    }

    // Task 8 이후 FreeText 계획으로 바꾼다 (지금은 EnsurePopup으로 패치된 사본인지 확인).
    [Fact]
    public async Task Recovery_PatchedCopy_FreeTextRestored()
    {
        string p = P("mixed.pdf");
        AnnotatedPdfFactory.CreateMixed(p);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.AnnotationsReady;
        s.PlanFactory = _ => PopupOnSquarePlan();
        var store = new RecoveryStore(P("recovery"), null, new PdfSharpAnnotationPatcher(() => []));

        await store.WriteAsync(s);

        var e = Assert.Single(store.List());
        Assert.Equal(2, PopupCount(e.RecoveryPdfPath));   // 원래 Popup 1 + 패치로 붙은 Popup 1
        Assert.Empty(Directory.GetFiles(P("recovery"), "*.tmp"));
    }

    [Fact]
    public async Task Recovery_PlanButNoPatcher_Throws_KeepsPreviousRecovery()
    {
        string p = P("mixed.pdf");
        AnnotatedPdfFactory.CreateMixed(p);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.AnnotationsReady;
        var store = new RecoveryStore(P("recovery"));
        await store.WriteAsync(s);
        string pdf = Assert.Single(store.List()).RecoveryPdfPath;
        var before = File.ReadAllBytes(pdf);

        s.PlanFactory = _ => PopupOnSquarePlan();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(s));

        Assert.Equal(before, File.ReadAllBytes(pdf));
        Assert.Empty(Directory.GetFiles(P("recovery"), "*.tmp"));
    }
}
