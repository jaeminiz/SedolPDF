using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;
using PdfEditor.Document;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Documents;

public class PageCommandsViewModelTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-pc-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly FakePrompts _p = new();
    private readonly List<DocumentSession> _sessions = [];
    private FileWorkflow _wf = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _wf = new FileWorkflow(_t, new SaveService([]), new SettingsStore(Path.Combine(_dir, "settings.json")), _p);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>쪽 너비가 100, 110, 120... 으로 달라서 너비로 쪽을 구별한다.</summary>
    private string Make(string name, int pages)
    {
        string path = P(name);
        TestPdfFactory.CreateLabeled(path, Enumerable.Range(0, pages).Select(i => (100f + 10 * i, 200f)).ToList());
        return path;
    }

    private async Task<(DocumentSession S, DocumentTabViewModel Tab, PageCommandsViewModel Vm)> OpenAsync(string path)
    {
        var s = await _wf.OpenAsync(path);
        Assert.NotNull(s);
        _sessions.Add(s!);
        var tab = new DocumentTabViewModel(s, s!.DisplayName, null);
        return (s, tab, new PageCommandsViewModel(tab, _wf, _p));
    }

    private static float[] Widths(DocumentSession s) => s.Pages.Select(p => p.WidthPt).ToArray();

    [Fact]
    public async Task Delete_Disabled_WhenAllSelected()
    {
        var (s, tab, vm) = await OpenAsync(Make("a.pdf", 3));
        Assert.True(vm.DeleteCommand.CanExecute(null));
        tab.SelectedPages = new HashSet<PageId>(s.Pages.Select(p => p.Id));
        Assert.False(vm.DeleteCommand.CanExecute(null));
        tab.SelectedPages = new HashSet<PageId>(s.Pages.Take(2).Select(p => p.Id));
        Assert.True(vm.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Delete_Disabled_WhenSinglePageDocument()
    {
        var (_, _, vm) = await OpenAsync(Make("one.pdf", 1));
        Assert.False(vm.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Delete_UsesSelection_ElseCurrentPage()
    {
        var (s, tab, vm) = await OpenAsync(Make("a.pdf", 4));
        tab.SelectedPages = new HashSet<PageId> { s.Pages[1].Id, s.Pages[2].Id };
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal([100f, 130f], Widths(s));

        tab.SelectedPages = new HashSet<PageId>();
        tab.CurrentPageIndex = 1;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal([100f], Widths(s));
    }

    [Fact]
    public async Task Commands_Disabled_WhenEditRestricted()
    {
        string path = P("r.pdf");
        TestPdfFactory.CreateOwnerRestricted(path, allowPrint: true, allowModify: false, allowCopy: true);
        var (s, tab, vm) = await OpenAsync(path);
        Assert.False(s.Permissions.CanEditPages);
        Assert.True(tab.IsEditRestricted);
        foreach (var c in new[] { vm.DeleteCommand, vm.RotateLeftCommand, vm.RotateRightCommand, vm.MoveUpCommand, vm.MoveDownCommand,
                     vm.MoveToCommand, vm.InsertBlankCommand, vm.InsertFromFileCommand, vm.MergeCommand })
            Assert.False(c.CanExecute(null));
    }

    [Fact]
    public async Task RotateRight_ThenUndo_RestoresRotation()
    {
        var (s, _, vm) = await OpenAsync(Make("a.pdf", 2));
        Assert.Equal(100f, s.Pages[0].WidthPt);
        await vm.RotateRightCommand.ExecuteAsync(null);
        Assert.Equal(200f, s.Pages[0].WidthPt);
        Assert.Equal(100f, s.Pages[0].HeightPt);
        Assert.True(vm.UndoCommand.CanExecute(null));
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(100f, s.Pages[0].WidthPt);
        Assert.True(vm.RedoCommand.CanExecute(null));
        await vm.RedoCommand.ExecuteAsync(null);
        Assert.Equal(200f, s.Pages[0].WidthPt);
    }

    [Fact]
    public async Task RotateLeft_RotatesSelectedOnly()
    {
        var (s, tab, vm) = await OpenAsync(Make("a.pdf", 3));
        tab.SelectedPages = new HashSet<PageId> { s.Pages[2].Id };
        await vm.RotateLeftCommand.ExecuteAsync(null);
        Assert.Equal([100f, 110f, 200f], Widths(s));
    }

    [Fact]
    public async Task MoveUp_FirstPage_Disabled()
    {
        var (s, tab, vm) = await OpenAsync(Make("a.pdf", 3));
        tab.CurrentPageIndex = 0;
        Assert.False(vm.MoveUpCommand.CanExecute(null));
        Assert.True(vm.MoveDownCommand.CanExecute(null));
        tab.CurrentPageIndex = 2;
        Assert.True(vm.MoveUpCommand.CanExecute(null));
        Assert.False(vm.MoveDownCommand.CanExecute(null));

        tab.CurrentPageIndex = 0;
        await vm.MoveDownCommand.ExecuteAsync(null);
        Assert.Equal([110f, 100f, 120f], Widths(s));
        Assert.Equal(1, tab.CurrentPageIndex);   // 옮긴 쪽을 따라간다
        await vm.MoveUpCommand.ExecuteAsync(null);
        Assert.Equal([100f, 110f, 120f], Widths(s));
    }

    [Fact]
    public async Task DragMove_UnselectedPage_MovesOnlyThatPage_UndoRestores()
    {
        var (s, tab, vm) = await OpenAsync(Make("a.pdf", 4));
        tab.SelectedPages = new HashSet<PageId> { s.Pages[1].Id };
        // 3번 쪽(번호 2)을 맨 앞으로: 선택 밖 쪽을 끌면 그 쪽만 움직인다
        await vm.MovePagesToDropAsync(s.Pages[2].Id, 0);
        Assert.Equal([120f, 100f, 110f, 130f], Widths(s));
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal([100f, 110f, 120f, 130f], Widths(s));
    }

    [Fact]
    public async Task DragMove_SelectedPages_MoveTogether_InOneUndo()
    {
        var (s, tab, vm) = await OpenAsync(Make("a.pdf", 5));
        tab.SelectedPages = new HashSet<PageId> { s.Pages[0].Id, s.Pages[1].Id };
        // 1·2쪽을 끌어 5쪽 앞(drop=4)에 놓기 → 3,4,1,2,5
        await vm.MovePagesToDropAsync(s.Pages[0].Id, 4);
        Assert.Equal([120f, 130f, 100f, 110f, 140f], Widths(s));
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal([100f, 110f, 120f, 130f, 140f], Widths(s));
        Assert.False(vm.UndoCommand.CanExecute(null));   // 한 번으로 모두 복원
    }

    [Fact]
    public async Task DragMove_SamePlace_DoesNothing_EndDrop_GoesLast()
    {
        var (s, _, vm) = await OpenAsync(Make("a.pdf", 3));
        await vm.MovePagesToDropAsync(s.Pages[1].Id, 1);   // 자기 앞
        await vm.MovePagesToDropAsync(s.Pages[1].Id, 2);   // 자기 뒤
        Assert.False(vm.UndoCommand.CanExecute(null));
        await vm.MovePagesToDropAsync(s.Pages[0].Id, 3);   // 맨 끝
        Assert.Equal([110f, 120f, 100f], Widths(s));
    }

    [Theory]
    [InlineData(new[] { 2 }, 0, 0)]
    [InlineData(new[] { 0 }, 3, 2)]
    [InlineData(new[] { 0, 1 }, 4, 2)]
    [InlineData(new[] { 0, 2 }, 5, 3)]   // 떨어진 쪽은 한데 모인다
    public void TryMoveDestination_Computes(int[] moving, int drop, int expected)
        => Assert.Equal(expected, PageCommandsViewModel.TryMoveDestination(moving, drop, 5));

    [Theory]
    [InlineData(new[] { 1 }, 1)]
    [InlineData(new[] { 1 }, 2)]
    [InlineData(new[] { 1, 2 }, 1)]
    [InlineData(new[] { 1, 2 }, 3)]
    public void TryMoveDestination_NoChange_IsNull(int[] moving, int drop)
        => Assert.Null(PageCommandsViewModel.TryMoveDestination(moving, drop, 5));

    [Fact]
    public async Task MoveTo_UsesDialogAnswer()
    {
        var (s, tab, vm) = await OpenAsync(Make("a.pdf", 4));
        tab.SelectedPages = new HashSet<PageId> { s.Pages[3].Id };
        _p.MoveTargetAnswer = new MoveTarget(MoveTargetKind.BeforePage, 2);
        await vm.MoveToCommand.ExecuteAsync(null);
        Assert.Equal([100f, 130f, 110f, 120f], Widths(s));

        tab.SelectedPages = new HashSet<PageId> { s.Pages[0].Id };
        _p.MoveTargetAnswer = new MoveTarget(MoveTargetKind.Last, 0);
        await vm.MoveToCommand.ExecuteAsync(null);
        Assert.Equal([130f, 110f, 120f, 100f], Widths(s));

        _p.MoveTargetAnswer = null;   // 취소
        await vm.MoveToCommand.ExecuteAsync(null);
        Assert.Equal([130f, 110f, 120f, 100f], Widths(s));
    }

    [Fact]
    public async Task InsertBlank_AfterCurrent_SameSize_AndUndo()
    {
        var (s, tab, vm) = await OpenAsync(Make("a.pdf", 3));
        tab.CurrentPageIndex = 1;
        await vm.InsertBlankCommand.ExecuteAsync(null);
        Assert.Equal([100f, 110f, 110f, 120f], Widths(s));
        Assert.Equal(2, tab.CurrentPageIndex);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal([100f, 110f, 120f], Widths(s));
    }

    [Fact]
    public async Task InsertFromFile_Range_InsertsAtChosenPosition()
    {
        var (s, tab, vm) = await OpenAsync(Make("a.pdf", 2));
        string src = P("src.pdf");
        TestPdfFactory.CreateLabeled(src, [(300f, 200f), (310f, 200f), (320f, 200f)]);
        _p.PickedFile = src;
        _p.InsertRequest = new InsertPagesRequest(InsertPosition.First, "2-3");
        await vm.InsertFromFileCommand.ExecuteAsync(null);
        Assert.Equal([310f, 320f, 100f, 110f], Widths(s));

        tab.CurrentPageIndex = 0;
        _p.InsertRequest = new InsertPagesRequest(InsertPosition.AfterCurrent, "");
        await vm.InsertFromFileCommand.ExecuteAsync(null);
        Assert.Equal([310f, 300f, 310f, 320f, 320f, 100f, 110f], Widths(s));
    }

    [Fact]
    public async Task InsertFromFile_InvalidRange_ShowsErrorNoChange()
    {
        var (s, _, vm) = await OpenAsync(Make("a.pdf", 2));
        string src = P("src.pdf");
        TestPdfFactory.CreateLabeled(src, [(300f, 200f), (310f, 200f)]);
        _p.PickedFile = src;
        _p.InsertRequest = new InsertPagesRequest(InsertPosition.Last, "1-9");
        await vm.InsertFromFileCommand.ExecuteAsync(null);
        Assert.Equal([100f, 110f], Widths(s));
        Assert.Contains(Strings.Err_PageRangeInvalid, _p.Errors);
        Assert.False(s.Commands.CanUndo);
    }

    [Fact]
    public async Task InsertFromFile_EncryptedSource_AsksPassword()
    {
        var (s, _, vm) = await OpenAsync(Make("a.pdf", 1));
        string src = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(src, 2, 300, 200, "secret");
        _p.PickedFile = src;
        _p.Passwords.Enqueue("secret");
        _p.InsertRequest = new InsertPagesRequest(InsertPosition.Last, "");
        await vm.InsertFromFileCommand.ExecuteAsync(null);
        Assert.Equal(3, s.Pages.Length);
    }

    [Fact]
    public async Task Merge_CreatesNewDocument_InChosenOrder()
    {
        var (s, _, vm) = await OpenAsync(Make("a.pdf", 2));
        string b = P("b.pdf");
        TestPdfFactory.CreateLabeled(b, [(300f, 200f), (310f, 200f), (320f, 200f)]);
        _p.PickedFiles = [P("a.pdf"), b];
        _p.MergeOrder = [b, P("a.pdf")];
        DocumentSession? created = null;
        vm.SessionCreated += x => created = x;
        await vm.MergeCommand.ExecuteAsync(null);
        Assert.NotNull(created);
        _sessions.Add(created!);
        Assert.Equal([300f, 310f, 320f, 100f, 110f], Widths(created!));
        Assert.Equal(string.Format(Strings.Doc_Untitled, 1), created!.DisplayName);
        Assert.True(created.IsDirty);
        Assert.Equal(2, s.Pages.Length);   // 현재 문서는 그대로
    }

    [Fact]
    public async Task Merge_Cancelled_CreatesNothing()
    {
        var (_, _, vm) = await OpenAsync(Make("a.pdf", 1));
        _p.PickedFiles = [P("a.pdf")];
        _p.MergeOrder = null;
        bool raised = false;
        vm.SessionCreated += _ => raised = true;
        await vm.MergeCommand.ExecuteAsync(null);
        Assert.False(raised);
    }

    [Fact]
    public async Task Merge_PasswordCancelled_CreatesNothing()
    {
        var (_, _, vm) = await OpenAsync(Make("a.pdf", 1));
        string enc = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(enc, 1, 300, 200, "secret");
        _p.PickedFiles = [P("a.pdf"), enc];
        _p.MergeOrder = [P("a.pdf"), enc];
        _p.Passwords.Enqueue(null);
        bool raised = false;
        vm.SessionCreated += _ => raised = true;
        await vm.MergeCommand.ExecuteAsync(null);
        Assert.False(raised);
    }

    [Fact]
    public async Task EditedDocument_SavedAndReopened_KeepsOrderAndRotation()
    {
        // 삭제 → 회전 → 이동 → 빈 쪽 삽입 → 파일에서 삽입 후 저장하고, 다시 열어 쪽 순서와 회전이 같은지 본다.
        // (Chrome·Acrobat·PDF-XChange로 여는 확인은 사용자가 docs/checklists/viewer-compat.md 목록으로 직접 한다.)
        var (s, tab, vm) = await OpenAsync(Make("a.pdf", 5));          // 100 110 120 130 140
        tab.SelectedPages = new HashSet<PageId> { s.Pages[1].Id };
        await vm.DeleteCommand.ExecuteAsync(null);                       // 100 120 130 140
        tab.SelectedPages = new HashSet<PageId> { s.Pages[2].Id };
        await vm.RotateRightCommand.ExecuteAsync(null);                  // 100 120 130(r) 140
        tab.SelectedPages = new HashSet<PageId> { s.Pages[3].Id };
        _p.MoveTargetAnswer = new MoveTarget(MoveTargetKind.First, 0);
        await vm.MoveToCommand.ExecuteAsync(null);                       // 140 100 120 130(r)
        tab.SelectedPages = new HashSet<PageId>();
        tab.CurrentPageIndex = 0;
        await vm.InsertBlankCommand.ExecuteAsync(null);                  // 140 140b 100 120 130(r)
        string src = P("src.pdf");
        TestPdfFactory.CreateLabeled(src, [(300f, 200f), (310f, 200f)]);
        _p.PickedFile = src;
        _p.InsertRequest = new InsertPagesRequest(InsertPosition.Last, "2");
        await vm.InsertFromFileCommand.ExecuteAsync(null);               // ... 310

        var expected = Widths(s);
        Assert.Equal([140f, 140f, 100f, 120f, 200f, 310f], expected);   // 회전한 쪽(130x200)은 너비 200

        string saved = P("saved.pdf");
        _p.SavePicks.Enqueue(saved);
        Assert.True(await _wf.SaveAsAsync(s));

        var reopened = await DocumentSession.OpenAsync(_t, saved, null);
        _sessions.Add(reopened);
        Assert.Equal(expected, Widths(reopened));
        Assert.Equal(s.Pages.Select(p => p.HeightPt), reopened.Pages.Select(p => p.HeightPt));
    }
    // ---- 2026-10-20 사용자 요청: 썸네일에서 페이지 추가(끌어다 놓기)·추출 ----

    [Fact]
    public async Task InsertFilesAt_DropBetweenPages_AllPagesInOrder_UndoPerFile()
    {
        var (s, _, vm) = await OpenAsync(Make("base.pdf", 3));   // 100,110,120
        string a = P("a.pdf"), b = P("b.pdf");
        TestPdfFactory.CreateLabeled(a, [(300, 200), (310, 200)]);
        TestPdfFactory.CreateLabeled(b, [(400, 200)]);
        await vm.InsertFilesAtAsync([a, b], 1);
        Assert.Equal([100f, 300f, 310f, 400f, 110f, 120f], Widths(s));
        await s.Commands.UndoAsync();
        Assert.Equal([100f, 300f, 310f, 110f, 120f], Widths(s));
        Assert.Empty(_p.Errors);
    }

    [Theory]
    [InlineData(0, false, 3, 0)]
    [InlineData(0, true, 3, 1)]
    [InlineData(2, true, 3, 3)]
    [InlineData(-1, false, 3, 3)]
    public void DropIndex_UpperHalfBefore_LowerHalfAfter_OutsideEnd(int over, bool lower, int count, int expected)
        => Assert.Equal(expected, PdfEditor.App.Panels.ThumbnailsViewModel.DropIndex(over, lower, count));

    [Fact]
    public async Task Extract_SelectedPages_SuggestsName_WritesNewPdf()
    {
        var (s, tab, vm) = await OpenAsync(Make("doc.pdf", 4));
        tab.SelectedPages = new HashSet<PageId>([s.Pages[1].Id, s.Pages[3].Id]);
        string target = P("out.pdf");
        _p.SavePicks.Enqueue(target);
        await vm.ExtractCommand.ExecuteAsync(null);
        Assert.Empty(_p.Errors);
        Assert.Equal("doc_2-4쪽.pdf", _p.SuggestedNames[^1]);
        var widths = await _t.InvokeAsync(() =>
        {
            using var d = PdfDocument.Open(_t, target);
            return Enumerable.Range(0, d.PageCount).Select(i => d.GetPageSizePt(i).Width).ToArray();
        });
        Assert.Equal([110f, 130f], widths);
        Assert.Single(_p.Infos);
        Assert.Equal(4, s.Pages.Length);
    }
}
