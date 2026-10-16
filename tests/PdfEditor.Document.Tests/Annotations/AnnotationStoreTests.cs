using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests.Annotations;

public class AnnotationStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-astore-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public AnnotationStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<DocumentSession> OpenMixed()
    {
        var p = Path.Combine(_dir, "mixed.pdf");
        AnnotatedPdfFactory.CreateMixed(p);
        var s = await DocumentSession.OpenAsync(_t, p, null);
        var originals = AnnotationReader.Read(File.ReadAllBytes(p), null, [0])[0];
        await s.Thread.InvokeAsync(() => { s.Annotations.AttachOriginals(s.Pages[0].Id, originals); s.Publish(); });
        return s;
    }

    private static Annotation NewSquare(DocumentSession s) => new()
    {
        Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(10, 10, 50, 50),
    };

    private async Task<DocumentSession> CreateWithOne()
    {
        var s = await DocumentSession.CreateNewAsync(_t);
        var page = s.Pages[0].Id;
        var a = NewSquare(s);
        await s.Thread.InvokeAsync(() => { s.Annotations.Apply(page, null, a); s.Publish(); });
        return s;
    }

    [Fact]
    public async Task Add_AppearsInSnapshotAsModelDrawn_ChangedRaised()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var page = s.Pages[0].Id;
        int raised = 0;
        s.Annotations.Changed += e => { raised++; Assert.Contains(page, e.Pages); };
        var a = NewSquare(s);
        await s.Thread.InvokeAsync(() => { s.Annotations.Apply(page, null, a); s.Publish(); });
        var items = s.Annotations.Get(page).Items;
        Assert.Single(items);
        Assert.True(items[0].ModelDrawn);
        Assert.Same(a, items[0].Data);
        Assert.Equal(1, raised);
        Assert.True(await s.Thread.InvokeAsync(() => s.Annotations.HasUnflushedChanges));
        Assert.Empty(s.Annotations.Get(new PageId(9999)).Items);
    }

    [Fact]
    public async Task AttachOriginals_EditableOnly_NotModelDrawn()
    {
        await using var s = await OpenMixed();
        var items = s.Annotations.Get(s.Pages[0].Id).Items;
        Assert.Equal(9, items.Length);
        Assert.All(items, i => Assert.False(i.ModelDrawn));
        Assert.False(await s.Thread.InvokeAsync(() => s.Annotations.HasUnflushedChanges));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Thread.InvokeAsync(
            () => s.Annotations.AttachOriginals(s.Pages[0].Id, [])));
    }

    [Fact]
    public async Task TouchOriginal_BecomesModelDrawn_BumpsAnnotRevisionOnce()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        int ver = s.Pages[0].Version;
        var first = s.Annotations.Get(page).Items[0].Data;   // PDF 번호 1
        var moved1 = first with { Rect = new PdfRectF(1, 1, 2, 2) };
        var moved2 = moved1 with { Rect = new PdfRectF(3, 3, 4, 4) };
        await s.Thread.InvokeAsync(() =>
        {
            s.Annotations.Apply(page, first, moved1); s.Publish();
            s.Annotations.Apply(page, moved1, moved2); s.Publish();
        });
        Assert.Equal(1, s.Pages[0].AnnotRevision);
        Assert.Equal(ver, s.Pages[0].Version);
        Assert.True(s.Annotations.Get(page).Items[0].ModelDrawn);
        var hidden = await s.Thread.InvokeAsync(() => s.Annotations.HiddenPdfIndices(page));
        Assert.Equal(new[] { 1 }, hidden.OrderBy(x => x));
    }

    [Fact]
    public async Task DeleteOriginal_HiddenIndexKept_NotInSnapshot()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var first = s.Annotations.Get(page).Items[0].Data;
        await s.Thread.InvokeAsync(() => { s.Annotations.Apply(page, first, null); s.Publish(); });
        Assert.Equal(8, s.Annotations.Get(page).Items.Length);
        Assert.DoesNotContain(s.Annotations.Get(page).Items, i => i.Data.Id == first.Id);
        var hidden = await s.Thread.InvokeAsync(() => s.Annotations.HiddenPdfIndices(page));
        Assert.Contains(1, hidden);
        Assert.Equal(1, s.Pages[0].AnnotRevision);
    }

    [Fact]
    public async Task UndoTouch_StaysModelDrawn()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var first = s.Annotations.Get(page).Items[0].Data;
        var moved = first with { Rect = new PdfRectF(1, 1, 2, 2) };
        await s.Thread.InvokeAsync(() =>
        {
            s.Annotations.Apply(page, first, moved);
            s.Annotations.Apply(page, moved, first);
            s.Publish();
        });
        var item = s.Annotations.Get(page).Items[0];
        Assert.Same(first, item.Data);
        Assert.True(item.ModelDrawn);
        Assert.Equal(1, s.Pages[0].AnnotRevision);
    }

    [Fact]
    public async Task Apply_StaleBefore_Throws()
    {
        await using var s = await CreateWithOne();
        var page = s.Pages[0].Id;
        var old = s.Annotations.Get(page).Items[0].Data;
        var next = old with { BorderWidth = 5 };
        await s.Thread.InvokeAsync(() => s.Annotations.Apply(page, old, next));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Thread.InvokeAsync(
            () => s.Annotations.Apply(page, old, old with { BorderWidth = 7 })));
        // 같은 Id를 다시 추가해도 예외
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Thread.InvokeAsync(
            () => s.Annotations.Apply(page, null, next)));
    }

    [Fact]
    public async Task NewId_UniqueAcrossThreads()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var ids = new System.Collections.Concurrent.ConcurrentBag<AnnotId>();
        Parallel.For(0, 10000, _ => ids.Add(s.Annotations.NewId()));
        Assert.Equal(10000, ids.Distinct().Count());
    }
}
