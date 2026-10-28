using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Tests.Viewer.Objects;
using PdfEditor.App.Viewer;
using PdfEditor.Document;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TextEdit.FindReplace;
using PdfEditor.TextEdit.Tests;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit.Abstractions;

namespace PdfEditor.App.Tests.Panels;

/// <summary>
/// 0.4.0 Task 6: 찾아 바꾸기 패널 모델·패널·명령 연결. 헤드리스(합성 입력)이며 실제 한글 IME·실제 마우스·다른 뷰어는 자동으로 못 잡는다(사용자 최소 점검).
/// </summary>
[Collection("TabSettings")]
public class FindReplaceViewModelTests(ITestOutputHelper log) : ObjectUiTestBase
{
    private const string Old = DrawingIdPdfFactory.Id;   // H-2501

    static FindReplaceViewModelTests() => FindReplaceViewModel.PlanDelayMs = 0;

    /// <summary>같은 쪽을 pages번 이은 문서(쪽마다 번호가 3곳).</summary>
    private static void MultiPage(string path, IdCase c, int pages, bool legend = true)
    {
        string one = path + ".one.pdf";
        DrawingIdPdfFactory.Create(one, c, Old, legend);
        using var src = PdfReader.Open(one, PdfDocumentOpenMode.Import);
        using var dst = new PdfDocument();
        for (int i = 0; i < pages; i++) dst.AddPage(src.Pages[0]);
        dst.Save(path);
        File.Delete(one);
    }

    private async Task<(DocumentSession S, DocumentTabViewModel Tab, FindReplaceViewModel Vm)> Open(IdCase c = IdCase.Whole, int pages = 1,
        bool legend = true, string newText = "H-1052", string? password = null)
    {
        var s = await OpenAsync(p =>
        {
            if (password is not null) { DrawingIdPdfFactory.Create(p + ".src.pdf", c, Old, legend); Encrypt(p + ".src.pdf", p, password); }
            else MultiPage(p, c, pages, legend);
        }, password);
        var tab = new DocumentTabViewModel(s, s.DisplayName, null);
        var vm = new FindReplaceViewModel(tab) { FindText = Old, ReplaceText = newText, MatchCase = true, WholeWord = true };
        return (s, tab, vm);
    }

    private static void Encrypt(string src, string dst, string pw)
    {
        using var d = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
        d.SecuritySettings.UserPassword = pw;
        d.SecuritySettings.OwnerPassword = pw + "-owner";
        d.Save(dst);
    }

    private async Task<int> Count(DocumentSession s, string text) { using var f = await FindReplaceService.FindAsync(s, new FindOptions { Query = text, MatchCase = true, WholeWord = true }); return f.Matches.Count(); }

    private SaveService Svc => new([], Services.Appearance, Services.Patcher, Services.Assets);

    // ------------------------------------------------------------------ 미리보기

    [Fact]
    public async Task FindReplaceViewModel_PreviewListsAllMatches()
    {
        var (s, _, vm) = await Open(pages: 2);
        await vm.FindCommand.ExecuteAsync(null);

        Assert.Equal(6, vm.Rows.Count);   // 쪽마다 3곳
        Assert.All(vm.Rows, r => { Assert.True(r.Ok); Assert.True(r.IsChecked); Assert.Equal(Old, r.Match); });
        Assert.Equal([1, 1, 1, 2, 2, 2], vm.Rows.Select(r => r.Hit.PageIndexAtSearch + 1).ToArray());
        Assert.Contains(vm.Rows, r => r.After.Contains("REV"));   // 문장 속 번호는 앞뒤 글이 보인다
        Assert.All(vm.Rows, r => Assert.NotEmpty(r.Hit.RectsPt));
        Assert.Equal(string.Format(Strings.Replace_Found, 6, 6), vm.StatusText);
        // 찾기만으로는 문서가 바뀌지 않는다
        Assert.False(s.Content.HasEdits);
        Assert.False(s.Commands.CanUndo);
        Assert.False(s.IsDirty);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task EmptyFindText_DoesNothing()
    {
        var (_, _, vm) = await Open();
        vm.FindText = "";
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public async Task NoMatch_ShowsMessage()
    {
        var (_, _, vm) = await Open();
        vm.FindText = "ZZZ-9999";
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Empty(vm.Rows);
        Assert.Equal(Strings.Replace_NoResults, vm.StatusText);
    }

    [Fact]
    public async Task Preview_ShowsFontStateAndAppFontNotice()
    {
        // 범례 없는 표본: 서브셋에 6·4가 없어 새 번호의 낱말은 앱 글꼴(N1)
        var (_, _, vm) = await Open(legend: false, newText: "H-2564");
        await vm.FindCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Equal(Strings.Replace_FontApp, r.FontLabel));
        Assert.Contains(string.Format(Strings.Replace_AppFontNotice, 3), vm.NoticeText);

        // 글이 서브셋 안의 글자로만 이루어지면 원래 글꼴로 바꾼다(미리보기가 다시 계산된다)
        vm.ReplaceText = "H-1052";
        await Task.Delay(300);
        Assert.All(vm.Rows, r => Assert.Equal(Strings.Replace_FontOriginal, r.FontLabel));
        Assert.DoesNotContain("대체", vm.NoticeText);
    }

    [Fact]
    public void EveryRefusal_HasAReadableText()
    {
        foreach (var r in Enum.GetValues<WordRefusal>().Where(x => x != WordRefusal.None))
            Assert.False(string.IsNullOrWhiteSpace(ReplaceRow.RefusalText(r)), r.ToString());
    }

    // ------------------------------------------------------------------ 바꾸기

    [Fact]
    public async Task ReplaceAll_FirstPressOnlyPreviews_SecondPressReplaces()
    {
        var (s, _, vm) = await Open();
        await vm.ReplaceAllCommand.ExecuteAsync(null);   // 미리보기가 없으면 찾기만 한다(U4)
        Assert.Equal(3, vm.Rows.Count);
        Assert.False(s.Commands.CanUndo);

        await vm.ReplaceAllCommand.ExecuteAsync(null);
        Assert.True(s.Commands.CanUndo);
        Assert.Equal(string.Format(Strings.Replace_Done, 3), vm.SummaryText);
        Assert.Empty(vm.Rows);   // 바꾼 낱말은 후보에서 빠진다
    }

    [Fact]
    public async Task ReplaceAll_CreatesSingleUndoStep()
    {
        var (s, _, vm) = await Open(pages: 3);
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal(9, vm.Rows.Count);
        await vm.ReplaceAllCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(Strings.Replace_Done, 9), vm.SummaryText);
        Assert.True(s.IsDirty);
        await s.Commands.UndoAsync();           // 실행 취소 한 번에 전부 원복
        Assert.False(s.Commands.CanUndo);
        Assert.False(s.Content.HasEdits);
        Assert.Equal(9, await Count(s, Old));
    }

    [Fact]
    public async Task UncheckedItemsAreSkipped()
    {
        var (s, _, vm) = await Open(pages: 2);
        await vm.FindCommand.ExecuteAsync(null);
        vm.Rows[0].IsChecked = false;
        vm.Rows[3].IsChecked = false;
        Assert.Equal(string.Format(Strings.Replace_Found, 6, 4), vm.StatusText);

        await vm.ReplaceAllCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(Strings.Replace_Done, 4), vm.SummaryText);
        Assert.Equal(2, vm.Rows.Count);    // 체크를 뺀 둘이 목록에 남는다
        Assert.Equal(2, await Count(s, Old));
    }

    [Fact]
    public async Task NothingChecked_ReplaceAllDoesNothing()
    {
        var (s, _, vm) = await Open();
        await vm.FindCommand.ExecuteAsync(null);
        vm.UncheckAllCommand.Execute(null);
        await vm.ReplaceAllCommand.ExecuteAsync(null);
        Assert.False(s.Commands.CanUndo);
        Assert.Equal(3, vm.Rows.Count);
        vm.CheckAllCommand.Execute(null);
        Assert.All(vm.Rows, r => Assert.True(r.IsChecked));
    }

    [Fact]
    public async Task ReplaceCurrent_FirstSelects_ThenReplacesOnlyThatOne()
    {
        var (s, _, vm) = await Open();
        await vm.FindCommand.ExecuteAsync(null);
        await vm.ReplaceCommand.ExecuteAsync(null);      // 고른 줄이 없으면 고르기만
        Assert.NotNull(vm.SelectedRow);
        Assert.False(s.Commands.CanUndo);

        await vm.ReplaceCommand.ExecuteAsync(null);      // 이제 그 한 곳만
        Assert.Equal(string.Format(Strings.Replace_Done, 1), vm.SummaryText);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(2, await Count(s, Old));
        Assert.NotNull(vm.SelectedRow);                  // 다음 줄로 넘어가 있다
        await s.Commands.UndoAsync();
        Assert.Equal(3, await Count(s, Old));
    }

    [Fact]
    public async Task EmptyReplacement_IsRefusedWithMessage()
    {
        var (s, _, vm) = await Open(newText: "");
        await vm.FindCommand.ExecuteAsync(null);
        Assert.False(vm.CanReplace);
        await vm.ReplaceAllCommand.ExecuteAsync(null);
        Assert.Equal(Strings.Replace_EmptyWith, vm.StatusText);
        Assert.False(s.Commands.CanUndo);
    }

    [Fact]
    public async Task DocumentChangedAfterFind_AsksToFindAgain()
    {
        var (s, _, vm) = await Open(pages: 2);
        await vm.FindCommand.ExecuteAsync(null);
        await s.Commands.ExecuteAsync(new RotatePagesCommand([s.Pages[0].Id], 1));   // 찾은 뒤 다른 편집

        await vm.ReplaceAllCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Replace_Stale, vm.StatusText);
        Assert.Empty(vm.Rows);
        Assert.Equal(1, s.Commands.Version);   // 아무것도 바꾸지 않았다(회전만)
    }

    [Fact]
    public async Task PageStructureChanged_ShowsSaveFirst()
    {
        var (s, _, vm) = await Open(pages: 2);
        await s.Commands.ExecuteAsync(new DeletePagesCommand([s.Pages[1].Id]));
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal(Strings.Replace_NeedSave, vm.StatusText);
        Assert.Empty(vm.Rows);
    }

    // ------------------------------------------------------------------ 범위

    [Fact]
    public async Task Scope_CurrentPageOnly()
    {
        var (s, tab, vm) = await Open(pages: 3);
        tab.CurrentPageIndex = 1;
        vm.Scope = ReplaceScope.CurrentPage;
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Equal(1, r.Hit.PageIndexAtSearch));

        await vm.ReplaceAllCommand.ExecuteAsync(null);
        Assert.Equal(6, await Count(s, Old));   // 다른 두 쪽은 그대로
    }

    [Fact]
    public async Task Scope_PageRange_AndInvalidRange()
    {
        var (s, _, vm) = await Open(pages: 4);
        vm.Scope = ReplaceScope.Range;
        vm.RangeText = "2-3";
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal([1, 1, 1, 2, 2, 2], vm.Rows.Select(r => r.Hit.PageIndexAtSearch).ToArray());

        vm.RangeText = "9";
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal(Strings.Replace_InvalidRange, vm.StatusText);
        vm.RangeText = "";
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal(Strings.Replace_InvalidRange, vm.StatusText);
        Assert.False(s.Commands.CanUndo);
    }

    // ------------------------------------------------------------------ 성능 (300쪽 문서 전체 찾기 — 수치는 결과 문서에 기록)

    [Fact]
    public async Task Perf_Find300Pages_TimeAndMemory()
    {
        var (s, _, vm) = await Open(pages: 300);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        proc.Refresh();
        long wsBefore = proc.WorkingSet64, managedBefore = GC.GetTotalMemory(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await vm.FindCommand.ExecuteAsync(null);
        long findMs = sw.ElapsedMilliseconds;
        proc.Refresh();
        long wsPeak = proc.PeakWorkingSet64, wsAfter = proc.WorkingSet64, managedAfter = GC.GetTotalMemory(false);
        Assert.Equal(900, vm.Rows.Count);
        sw.Restart();
        vm.ReplaceText = "H-1053";   // 미리보기 다시 계산(글꼴 상태)
        await Task.Delay(50);
        while (vm.IsBusy) await Task.Delay(20);
        long replanMs = sw.ElapsedMilliseconds;
        sw.Restart();
        await vm.ReplaceAllCommand.ExecuteAsync(null);
        long replaceMs = sw.ElapsedMilliseconds;
        log.WriteLine($"300쪽(쪽마다 3곳) 찾기+미리보기 {findMs} ms, 글 바꾼 뒤 미리보기 다시 계산 약 {replanMs} ms, 모두 바꾸기(900곳)+다시 찾기 {replaceMs} ms");
        log.WriteLine($"작업 집합 {wsBefore / 1048576.0:F0} → {wsAfter / 1048576.0:F0} MB (최대 {wsPeak / 1048576.0:F0} MB), 관리 힙 {managedBefore / 1048576.0:F1} → {managedAfter / 1048576.0:F1} MB");
        Assert.Equal(string.Format(Strings.Replace_Done, 900), vm.SummaryText);
        Assert.True(s.Commands.CanUndo);
    }

    // ------------------------------------------------------------------ 취소·진행

    [Fact]
    public async Task Cancel_StopsFindWithoutResults()
    {
        var (s, _, vm) = await Open(pages: 60);
        var task = vm.FindCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        vm.CancelCommand.Execute(null);
        await task;
        Assert.False(vm.IsBusy);
        Assert.False(s.Commands.CanUndo);
        // 취소한 뒤에도 다시 찾을 수 있다
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal(180, vm.Rows.Count);
    }

    // ------------------------------------------------------------------ 사용자 시나리오 (UserScenarioTests 방식)

    [Fact]
    public async Task Scenario_HullNumberReplaceAll_SaveReopen_OldNumberGone()
    {
        // 도면 번호 H-2501 → H-1052(원래 글꼴로 쓸 수 있는 글자), 4쪽, 모두 바꾸기 → 저장 → 다시 열기 → 옛 번호 0건
        var (s, _, vm) = await Open(pages: 4, newText: "H-1052");
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal(12, vm.Rows.Count);
        await vm.ReplaceAllCommand.ExecuteAsync(null);
        Assert.Equal(string.Format(Strings.Replace_Done, 12), vm.SummaryText);

        string saved = Path.Combine(Dir, "saved.pdf");
        await Svc.SaveAsync(s, saved);
        var s2 = await DocumentSession.OpenAsync(Thread, saved, null);
        try
        {
            Assert.Equal(0, await Count(s2, Old));
            Assert.Equal(12, await Count(s2, "H-1052"));
        }
        finally { await s2.DisposeAsync(); }
    }

    [Theory]
    [InlineData(IdCase.Rot90)]
    [InlineData(IdCase.PageRot90)]
    public async Task Scenario_RotatedTitleBlock_IsFoundAndReplaced(IdCase c)
    {
        var (s, _, vm) = await Open(c, pages: 2);
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal(6, vm.Rows.Count);
        Assert.Contains(vm.Rows, r => r.Item.Match.RotationDeg % 360 != 0 && r.Note.Length > 0);   // 회전 글은 표시된다

        vm.IncludeRotated = false;   // 회전 글 제외 옵션
        await vm.FindCommand.ExecuteAsync(null);
        Assert.True(vm.Rows.Count < 6);
        vm.IncludeRotated = true;
        await vm.FindCommand.ExecuteAsync(null);
        await vm.ReplaceAllCommand.ExecuteAsync(null);
        Assert.Equal(string.Format(Strings.Replace_Done, 6), vm.SummaryText);

        string saved = Path.Combine(Dir, $"rot-{c}.pdf");
        await Svc.SaveAsync(s, saved);
        var s2 = await DocumentSession.OpenAsync(Thread, saved, null);
        try { Assert.Equal(0, await Count(s2, Old)); Assert.Equal(6, await Count(s2, "H-1052")); }
        finally { await s2.DisposeAsync(); }
    }

    [Fact]
    public async Task Scenario_EncryptedDocument_FindsButCannotReplace_NoSilentFailure()
    {
        var (s, _, vm) = await Open(password: "pw1234");
        Assert.True(vm.HasBlockedReason);
        Assert.Equal(Strings.Replace_Encrypted, vm.BlockedReason);
        Assert.False(vm.CanReplace);

        await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Rows.Count);
        Assert.All(vm.Rows, r => { Assert.False(r.Ok); Assert.False(r.IsChecked); Assert.Contains(Strings.Replace_Refuse_Encrypted, r.Note); });
        Assert.Contains(Strings.Replace_Encrypted, vm.NoticeText);

        await vm.ReplaceAllCommand.ExecuteAsync(null);
        Assert.Equal(Strings.Replace_Encrypted, vm.StatusText);
        Assert.False(s.Commands.CanUndo);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public async Task Scenario_NoTextFont_ReportsUnsearchableOps()
    {
        var (_, _, vm) = await Open(IdCase.NoToUnicode);
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Empty(vm.Rows);
        Assert.Contains(string.Format(Strings.Replace_Unsearchable, 1, 1).Split("1")[0], vm.NoticeText);   // "글꼴 정보가 없어 찾지 못한 글 …"
        Assert.Contains("1", vm.NoticeText);
        Assert.Equal(Strings.Replace_NoResults, vm.StatusText);
    }

    // ------------------------------------------------------------------ 강조·이동

    [Fact]
    public async Task SelectingARow_RevealsItAndRaisesHighlightChange()
    {
        var (s, tab, vm) = await Open(pages: 3);
        await vm.FindCommand.ExecuteAsync(null);
        int changed = 0;
        vm.HighlightsChanged += () => changed++;
        (Pdf.Native.PdfRectF Rect, PageId Page)? revealed = null;
        tab.RevealRequested += (p, r) => revealed = (r, p);

        vm.SelectedRow = vm.Rows[7];   // 3쪽
        Assert.Equal(2, tab.CurrentPageIndex);
        Assert.NotNull(revealed);
        Assert.Equal(vm.Rows[7].Hit.Page, revealed!.Value.Page);
        Assert.True(revealed.Value.Rect.Right > revealed.Value.Rect.Left);
        Assert.True(changed > 0);

        int before = changed;
        vm.Rows[0].IsChecked = false;   // 체크를 바꾸면 화면 강조 색이 달라지므로 다시 그린다
        Assert.True(changed > before);

        await vm.NextCommand.ExecuteAsync(null);
        Assert.Same(vm.Rows[8], vm.SelectedRow);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Same(vm.Rows[0], vm.SelectedRow);   // 끝에서 처음으로
    }

    // ------------------------------------------------------------------ 패널 키 처리 (헤드리스)

    private static KeyEventArgs Key(Key k) => new() { RoutedEvent = InputElement.KeyDownEvent, Key = k };

    [Fact]
    public async Task Panel_EnterFinds_EscCloses_ImeKeysAreLeftToTheInputMethod()
    {
        var (_, _, vm) = await Open();
        bool closed = false;
        vm.CloseRequested += () => closed = true;

        TextBox? find = null;
        FindReplacePanel? panel = null;
        HeadlessUi.Run(() =>
        {
            if (!Avalonia.Application.Current!.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any())
                Avalonia.Application.Current.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            panel = new FindReplacePanel { DataContext = vm };
            var w = new Window { Width = 360, Height = 700, Content = panel };
            w.Show();
            w.UpdateLayout();
            find = panel.FindControl<TextBox>("FindBox")!;
            // 한글 조합이 끝나자마자 오는 Enter/Esc는 입력기 몫이라 찾기·닫기를 하지 않는다
            var ime = ImeCompositionGuard.For(find);
            long[] now = [5_000_000];
            ime.ClockOverride = () => now[0];
            var imeKey = Key(Avalonia.Input.Key.ImeProcessed);
            find.RaiseEvent(imeKey);
            var enterAfterIme = Key(Avalonia.Input.Key.Enter);
            find.RaiseEvent(enterAfterIme);
            var escAfterIme = Key(Avalonia.Input.Key.Escape);
            find.RaiseEvent(escAfterIme);
            return 0;
        });
        Assert.False(closed);
        Assert.Empty(vm.Rows);

        HeadlessUi.Run(() =>
        {
            var ime = ImeCompositionGuard.For(find!);
            ime.ForgetRecentEnd();
            var enter = Key(Avalonia.Input.Key.Enter);
            find!.RaiseEvent(enter);
            Assert.True(enter.Handled);
            return 0;
        });
        for (int i = 0; i < 400 && vm.Rows.Count == 0; i++) { HeadlessUi.Run(() => { Dispatcher.UIThread.RunJobs(); return 0; }); await Task.Delay(15); }
        Assert.Equal(3, vm.Rows.Count);   // Enter = 찾기

        HeadlessUi.Run(() =>
        {
            ImeCompositionGuard.For(find!).ForgetRecentEnd();
            var esc = Key(Avalonia.Input.Key.Escape);
            find!.RaiseEvent(esc);
            Assert.True(esc.Handled);
            return 0;
        });
        Assert.True(closed);   // Esc = 패널 닫기(문서 화면의 Esc 처리로 번지지 않음)
    }

    [Fact]
    public void Panel_Renders_AndListShowsRows()
    {
        // XAML 바인딩이 모두 맞는지: 잘못된 경로가 있으면 헤드리스 로그에 바인딩 오류가 남는 대신 여기서 컨트롤을 찾아 확인한다
        HeadlessUi.Run(() =>
        {
            if (!Avalonia.Application.Current!.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any())
                Avalonia.Application.Current.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            var panel = new FindReplacePanel();
            var w = new Window { Width = 360, Height = 700, Content = panel };
            w.Show();
            w.UpdateLayout();
            Assert.NotNull(panel.FindControl<ListBox>("ResultList"));
            Assert.NotEmpty(panel.GetVisualDescendants().OfType<CheckBox>());
            Assert.NotEmpty(panel.GetVisualDescendants().OfType<RadioButton>());
            return 0;
        });
    }
}
