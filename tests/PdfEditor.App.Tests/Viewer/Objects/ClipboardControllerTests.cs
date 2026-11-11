using System.Globalization;
using System.Resources;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Viewer.Objects;

/// <summary>0.9.0 Task 9: 개체·주석 복사/잘라내기/붙여넣기/복제 흐름(ClipboardController, Avalonia 없음).</summary>
public sealed class ClipboardControllerTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-clipctl-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly List<DocumentSession> _sessions = [];

    private static readonly DateTimeOffset Fixed = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ClipboardController NewController() => new() { Now = () => Fixed, Author = () => "tester" };

    private async Task<DocumentTabViewModel> OpenTab(Action<string>? create = null)
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        (create ?? (x => ContentPdfFactory.CreateDocumentLike(x)))(p);
        var s = await DocumentSession.OpenAsync(_t, p, null);
        _sessions.Add(s);
        await s.AnnotationsReady;
        return new DocumentTabViewModel(s, "a.pdf", null);
    }

    private static List<string> Notices(DocumentTabViewModel tab)
    {
        var list = new List<string>();
        tab.NoticeRequested += list.Add;
        return list;
    }

    private Task<SelectedObject> PickPath(DocumentTabViewModel tab) => _t.InvokeAsync(() =>
    {
        var s = tab.Session!;
        var id = s.Pages[0].Id;
        var e = s.GetObjectIndex(id).Entries.Where(x => x.Type == PdfPageObjectType.Path && !x.Ref.IsInserted && x.Bounds.Width < 400).OrderBy(x => x.Order).First();
        return new SelectedObject(id, e.Ref, e.Type, e.Bounds);
    });

    private Task<PdfRectF> BoundsOf(DocumentTabViewModel tab, ObjectRef r) => _t.InvokeAsync(() =>
        tab.Session!.GetObjectIndex(tab.Session.Pages[0].Id).Entries.First(e => e.Ref == r).Bounds);

    [Fact]
    public async Task Copy_NothingSelected_ReturnsFalse()
    {
        var tab = await OpenTab();
        var ctl = NewController();
        Assert.False(await ctl.CopyAsync(tab, cut: false));
        Assert.Null(ctl.Current);
    }

    [Fact]
    public async Task Paste_FromOtherSession_RaisesOtherDocumentNotice()
    {
        var a = await OpenTab(); var b = await OpenTab();
        var ctl = NewController();
        a.Objects.Select(await PickPath(a));
        Assert.True(await ctl.CopyAsync(a, cut: false));
        var notices = Notices(b);
        await ctl.PasteAsync(b, b.Session!.Pages[0].Id, 0);
        Assert.Equal([Strings.Msg_ObjCopy_OtherDocument], notices);
        Assert.False(ctl.CanPaste(b));
        Assert.True(ctl.CanPaste(a));
    }

    [Fact]
    public async Task Copy_WithoutCopyPermission_RaisesNoPermissionNotice()
    {
        var tab = await OpenTab(p => TestPdfFactory.CreateOwnerRestricted(p, allowPrint: true, allowModify: true, allowCopy: false));
        var ctl = NewController();
        var id = tab.Session!.Pages[0].Id;
        tab.Objects.Select(new SelectedObject(id, ObjectRef.Original(0), PdfPageObjectType.Path, new PdfRectF(10, 10, 50, 50)));
        var notices = Notices(tab);
        Assert.True(await ctl.CopyAsync(tab, cut: false));
        Assert.Equal([Strings.Msg_ObjCopy_NoPermission], notices);
        Assert.Null(ctl.Current);
    }

    [Fact]
    public async Task Paste_TwiceOnSamePage_OffsetsGrow()
    {
        var tab = await OpenTab();
        var ctl = NewController();
        var sel = await PickPath(tab);
        var id = sel.Page;
        tab.Objects.Select(sel);
        Assert.True(await ctl.CopyAsync(tab, cut: false));
        await ctl.PasteAsync(tab, id, 0);
        var first = tab.Objects.Selected!;
        Assert.True(first.Ref.IsInserted);
        var b1 = await BoundsOf(tab, first.Ref);
        await ctl.PasteAsync(tab, id, 0);
        var b2 = await BoundsOf(tab, tab.Objects.Selected!.Ref);
        Assert.NotEqual(first.Ref, tab.Objects.Selected.Ref);
        Assert.InRange(b1.Left, sel.Bounds.Left + 9.5f, sel.Bounds.Left + 10.5f);
        Assert.InRange(b1.Bottom, sel.Bounds.Bottom - 10.5f, sel.Bounds.Bottom - 9.5f);
        Assert.InRange(b2.Left - b1.Left, 9.5f, 10.5f);
        Assert.InRange(b2.Bottom - b1.Bottom, -10.5f, -9.5f);
    }

    [Fact]
    public async Task Cut_RemovesOriginal_OneUndoRestores()
    {
        var tab = await OpenTab();
        var ctl = NewController();
        var sel = await PickPath(tab);
        tab.Objects.Select(sel);
        Assert.True(await ctl.CopyAsync(tab, cut: true));
        Assert.NotNull(ctl.Current);
        bool Present() => _t.InvokeAsync(() => tab.Session!.GetObjectIndex(sel.Page).Entries.Any(e => e.Ref == sel.Ref)).GetAwaiter().GetResult();
        Assert.False(Present());
        Assert.Empty(tab.Objects.All);
        await tab.Session!.Commands.UndoAsync();   // 실행 취소 한 번
        Assert.True(Present());
    }

    [Fact]
    public async Task Duplicate_DoesNotChangeClipboard()
    {
        var tab = await OpenTab();
        var ctl = NewController();
        var sel = await PickPath(tab);
        tab.Objects.Select(sel);
        Assert.True(await ctl.CopyAsync(tab, cut: false));
        var before = ctl.Current;
        int count = await _t.InvokeAsync(() => tab.Session!.GetObjectIndex(sel.Page).Entries.Count);
        await ctl.DuplicateAsync(tab, 0);
        Assert.Same(before, ctl.Current);
        Assert.Equal(0, ctl.Current!.PasteCount);
        Assert.Equal(count + 1, await _t.InvokeAsync(() => tab.Session!.GetObjectIndex(sel.Page).Entries.Count));
        Assert.True(tab.Objects.Selected!.Ref.IsInserted);
        // 복제만 하는 경우(클립보드 없음)도 된다
        var other = NewController();
        tab.Objects.Select(sel);
        await other.DuplicateAsync(tab, 0);
        Assert.Null(other.Current);
        Assert.Equal(count + 2, await _t.InvokeAsync(() => tab.Session!.GetObjectIndex(sel.Page).Entries.Count));
    }

    [Fact]
    public async Task Paste_SelectsNewObjects()
    {
        var tab = await OpenTab();
        var ctl = NewController();
        var sel = await PickPath(tab);
        tab.Objects.Select(sel);
        await ctl.CopyAsync(tab, cut: false);
        await ctl.PasteAsync(tab, sel.Page, 0);
        var now = Assert.Single(tab.Objects.All);
        Assert.True(now.Ref.IsInserted);
        Assert.NotEqual(sel.Ref, now.Ref);
        await tab.Session!.Commands.UndoAsync();   // 붙여넣기 하나 = 실행 취소 한 번
        Assert.Equal(0, await _t.InvokeAsync(() => tab.Session!.GetObjectIndex(sel.Page).Entries.Count(e => e.Ref.IsInserted)));
    }

    [Fact]
    public async Task Annotations_CopyPaste_NewIdsSelected_UndoOnce()
    {
        var tab = await OpenTab(p => TestPdfFactory.CreateBlank(p, 1, 300, 300));
        var s = tab.Session!;
        var id = s.Pages[0].Id;
        var sq = new Annotation { Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(20, 20, 80, 60), Color = new PdfColor(200, 0, 0) };
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, sq));
        var ctl = NewController();
        tab.Selection.Set(id, [sq.Id]);
        Assert.True(await ctl.CopyAsync(tab, cut: false));
        await ctl.PasteAsync(tab, id, 0);
        var all = s.Annotations.Get(id).Items.Select(v => v.Data).ToList();
        Assert.Equal(2, all.Count);
        var pasted = all[1];
        Assert.Equal([pasted.Id], tab.Selection.Ids);
        Assert.Equal("tester", pasted.Author);
        Assert.Equal(Fixed, pasted.CreatedAt);
        Assert.Equal(30f, pasted.Rect.Left, 3);
        await s.Commands.UndoAsync();
        Assert.Single(s.Annotations.Get(id).Items);
    }

    [Fact]
    public void Strings_AllNewKeysExistInBothLanguages()
    {
        var rm = new ResourceManager("PdfEditor.App.Resources.Strings", typeof(Strings).Assembly);
        string[] keys =
        [
            "Msg_ObjCopy_OtherDocument", "Msg_ObjCopy_NoPermission", "Msg_ObjCopy_Partial", "Msg_DeletePage_HasCopies",
            "Menu_Cut", "Menu_Copy", "Menu_Paste", "Menu_Duplicate", "Menu_Delete",
        ];
        foreach (var k in keys)
        {
            string? ko = rm.GetString(k, CultureInfo.InvariantCulture);
            string? en = rm.GetString(k, CultureInfo.GetCultureInfo("en"));
            Assert.False(string.IsNullOrWhiteSpace(ko), k);
            Assert.False(string.IsNullOrWhiteSpace(en), k);
            Assert.NotEqual(ko, en);
        }
        Assert.Equal("다른 문서의 개체는 아직 붙일 수 없습니다", rm.GetString("Msg_ObjCopy_OtherDocument", CultureInfo.InvariantCulture));
        Assert.Equal("Some items could not be copied.", rm.GetString("Msg_ObjCopy_Partial", CultureInfo.GetCultureInfo("en")));
    }
}
