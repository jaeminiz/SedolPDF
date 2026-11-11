using Pdf.Native;
using PdfEditor.Document.Content;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests.Content;

/// <summary>0.9.0 Task 2: DuplicateObjectEdit의 화면 replay(종류별 화면 사본).</summary>
public sealed class DuplicateReplayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-dupreplay-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public DuplicateReplayTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<DocumentSession> OpenDocument(int pages = 1)
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateDocumentLike(p, pages: pages);
        return await DocumentSession.OpenAsync(_t, p, null);
    }

    private async Task<DocumentSession> OpenDrawing()
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateDrawingLike(p, gradient: false);
        return await DocumentSession.OpenAsync(_t, p, null);
    }

    private Task<T> On<T>(Func<T> f) => _t.InvokeAsync(f);

    private static PageObjectEntry Pick(DocumentSession s, PageId id, PdfPageObjectType type, Func<PageObjectEntry, bool>? where = null) =>
        s.GetObjectIndex(id).Entries.Where(e => e.Type == type && !e.Ref.IsInserted && (where?.Invoke(e) ?? true)).OrderBy(e => e.Ref.OriginalIndex).First();

    private static void AssertNear(PdfRectF expected, PdfRectF actual, float tol)
    {
        Assert.InRange(actual.Left, expected.Left - tol, expected.Left + tol);
        Assert.InRange(actual.Right, expected.Right - tol, expected.Right + tol);
        Assert.InRange(actual.Bottom, expected.Bottom - tol, expected.Bottom + tol);
        Assert.InRange(actual.Top, expected.Top - tol, expected.Top + tol);
    }

    private static PdfRectF Shift(PdfRectF r, float dx, float dy) => new(r.Left + dx, r.Bottom + dy, r.Right + dx, r.Top + dy);

    [Fact]
    public async Task Duplicate_SamePagePath_AddsObjectWithShiftedBounds()
    {
        await using var s = await OpenDocument();
        var id = s.Pages[0].Id;
        var (before, srcBounds, srcIdx) = await On(() =>
        {
            var e = Pick(s, id, PdfPageObjectType.Path, x => x.Bounds.Width < 400);
            return (s.GetLoadedPage(id).ObjectCount, e.Bounds, e.Ref.OriginalIndex);
        });
        long did = s.Content.NewId();
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new DuplicateObjectEdit(did, id, srcIdx, PdfMatrix.Translate(10, -10))]));
        await On(() =>
        {
            var r = s.GetReplay(id);
            Assert.Equal(before + 1, s.GetLoadedPage(id).ObjectCount);
            AssertNear(Shift(srcBounds, 10, -10), r.Resolve(ObjectRef.Inserted(did))!.Bounds, 0.5f);
            return 0;
        });
    }

    [Fact]
    public async Task Duplicate_Text_SameTextAndFontSize()
    {
        await using var s = await OpenDocument();
        var id = s.Pages[0].Id;
        int idx = await On(() => Pick(s, id, PdfPageObjectType.Text, x => (x.TextPreview ?? "").StartsWith("Arial")).Ref.OriginalIndex);
        long did = s.Content.NewId();
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new DuplicateObjectEdit(did, id, idx, PdfMatrix.Translate(0, -30))]));
        await On(() =>
        {
            var r = s.GetReplay(id);
            var tp = s.GetLoadedText(id);
            var o = r.Resolve(ObjectRef.Inserted(did))!;
            var orig = r.Resolve(ObjectRef.Original(idx))!;
            Assert.Equal(PdfPageObjectType.Text, o.Type);
            Assert.Equal(orig.GetText(tp), o.GetText(tp));
            Assert.Equal(orig.FontSize, o.FontSize);
            return 0;
        });
    }

    [Fact]
    public async Task Duplicate_Form_AddsImageObjectCoveringSourceBounds()
    {
        await using var s = await OpenDrawing();
        var id = s.Pages[0].Id;
        var (b, idx) = await On(() => { var e = Pick(s, id, PdfPageObjectType.Form); return (e.Bounds, e.Ref.OriginalIndex); });
        long did = s.Content.NewId();
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new DuplicateObjectEdit(did, id, idx, PdfMatrix.Translate(0, 0))]));
        await On(() =>
        {
            var o = s.GetReplay(id).Resolve(ObjectRef.Inserted(did))!;
            Assert.Equal(PdfPageObjectType.Image, o.Type);
            AssertNear(b, o.Bounds, 1f);
            return 0;
        });
    }

    [Fact]
    public async Task Duplicate_UsesOriginalLook_EvenIfSourceMovedEarlier()
    {
        await using var s = await OpenDocument();
        var id = s.Pages[0].Id;
        var (b, idx) = await On(() => { var e = Pick(s, id, PdfPageObjectType.Path, x => x.Bounds.Width < 400); return (e.Bounds, e.Ref.OriginalIndex); });
        long mv = s.Content.NewId(), did = s.Content.NewId();
        await s.Commands.ExecuteAsync(new ContentEditCommand(id,
        [
            new TransformEdit(mv, ObjectRef.Original(idx), PdfMatrix.Translate(50, 0)),
            new DuplicateObjectEdit(did, id, idx, PdfMatrix.Translate(0, 0)),
        ]));
        await On(() =>
        {
            var r = s.GetReplay(id);
            AssertNear(Shift(b, 50, 0), r.Resolve(ObjectRef.Original(idx))!.Bounds, 0.5f);
            AssertNear(b, r.Resolve(ObjectRef.Inserted(did))!.Bounds, 0.5f);
            return 0;
        });
    }

    [Fact]
    public async Task Duplicate_FromOtherPage_UsesSourcePageObject()
    {
        await using var s = await OpenDocument(pages: 2);
        var p1 = s.Pages[0].Id;
        var p2 = s.Pages[1].Id;
        var (b, idx) = await On(() => { var e = Pick(s, p1, PdfPageObjectType.Path, x => x.Bounds.Width < 400); return (e.Bounds, e.Ref.OriginalIndex); });
        long did = s.Content.NewId();
        await s.Commands.ExecuteAsync(new ContentEditCommand(p2, [new DuplicateObjectEdit(did, p1, idx, PdfMatrix.Identity)]));
        await On(() =>
        {
            AssertNear(b, s.GetReplay(p2).Resolve(ObjectRef.Inserted(did))!.Bounds, 0.5f);
            return 0;
        });
    }

    [Fact]
    public async Task Duplicate_ThenTransformAndDeactivate_Work()
    {
        await using var s = await OpenDocument();
        var id = s.Pages[0].Id;
        var (b, idx) = await On(() => { var e = Pick(s, id, PdfPageObjectType.Path, x => x.Bounds.Width < 400); return (e.Bounds, e.Ref.OriginalIndex); });
        long did = s.Content.NewId();
        await s.Commands.ExecuteAsync(new ContentEditCommand(id,
        [
            new DuplicateObjectEdit(did, id, idx, PdfMatrix.Identity),
            new TransformEdit(s.Content.NewId(), ObjectRef.Inserted(did), PdfMatrix.Translate(20, 0)),
        ]));
        await On(() => { AssertNear(Shift(b, 20, 0), s.GetReplay(id).Resolve(ObjectRef.Inserted(did))!.Bounds, 0.5f); return 0; });
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new DeactivateEdit(s.Content.NewId(), ObjectRef.Inserted(did))]));
        await On(() => { Assert.False(s.GetReplay(id).Resolve(ObjectRef.Inserted(did))!.IsActive); return 0; });
    }

    [Fact]
    public async Task Session_ObjectIndex_ListsDuplicate()
    {
        await using var s = await OpenDocument();
        var id = s.Pages[0].Id;
        int idx = await On(() => Pick(s, id, PdfPageObjectType.Path, x => x.Bounds.Width < 400).Ref.OriginalIndex);
        long did = s.Content.NewId();
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new DuplicateObjectEdit(did, id, idx, PdfMatrix.Translate(5, 5))]));
        await On(() => { Assert.Contains(s.GetObjectIndex(id).Entries, e => e.Ref == ObjectRef.Inserted(did)); return 0; });
    }
}
