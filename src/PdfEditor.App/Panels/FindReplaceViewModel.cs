using System.Collections.ObjectModel;
using System.Drawing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document;
using PdfEditor.Pages;
using PdfEditor.TextEdit.FindReplace;

namespace PdfEditor.App.Panels;

/// <summary>찾을 범위.</summary>
public enum ReplaceScope { All, CurrentPage, Range }

/// <summary>미리보기 목록의 한 줄(찾은 곳 하나). 체크를 빼면 "바꾸기"에서 제외된다.</summary>
public sealed partial class ReplaceRow : ObservableObject
{
    internal ReplaceRow(ReplaceItem item, SearchHit hit, string newText)
    {
        Item = item;
        Hit = hit;
        NewText = newText;
        _isChecked = item.Ok;
        Note = BuildNote(item);
    }

    public ReplaceItem Item { get; }
    /// <summary>화면 강조와 이동에 쓰는 위치(쪽 좌표 사각형).</summary>
    public SearchHit Hit { get; }
    public string NewText { get; }

    [ObservableProperty] private bool _isChecked;

    public bool Ok => Item.Ok;
    public string PageLabel => Hit.Label;
    public string Before => Item.Match.BeforeContext;
    public string Match => Item.Match.Text;
    public string After => Item.Match.AfterContext;
    public string Note { get; }
    public bool HasNote => Note.Length > 0;
    /// <summary>못 바꾸는 줄이면 이유(빨강), 대체 글꼴·겹침이면 주의(주황).</summary>
    public bool IsProblem => !Item.Ok;
    public string FontLabel => !Item.Ok ? "" : Item.Font == ReplaceFontKind.App && NewText.Length > 0 ? Strings.Replace_FontApp : Strings.Replace_FontOriginal;

    private static string BuildNote(ReplaceItem it)
    {
        var parts = new List<string>();
        if (!it.Ok) parts.Add(RefusalText(it.Refusal));
        else if (it.Overlap) parts.Add(Strings.Replace_Overlap);
        if (it.Match.RotationDeg % 360 != 0) parts.Add(string.Format(Strings.Replace_RotatedTag, ((it.Match.RotationDeg % 360) + 360) % 360));
        return string.Join(" · ", parts);
    }

    public static string RefusalText(WordRefusal r) => r switch
    {
        WordRefusal.InForm => Strings.Replace_Refuse_InForm,
        WordRefusal.QuoteOperator => Strings.Replace_Refuse_QuoteOperator,
        WordRefusal.ClipText => Strings.Replace_Refuse_ClipText,
        WordRefusal.NoFontSize => Strings.Replace_Refuse_NoFontSize,
        WordRefusal.AlreadyEdited => Strings.Replace_Refuse_AlreadyEdited,
        WordRefusal.Encrypted => Strings.Replace_Refuse_Encrypted,
        WordRefusal.PageMismatch => Strings.Replace_Refuse_PageMismatch,
        WordRefusal.ObjectNotFound => Strings.Replace_Refuse_ObjectNotFound,
        WordRefusal.MissingGlyphs => Strings.Replace_Refuse_MissingGlyphs,
        WordRefusal.NoAppFont => Strings.Replace_Refuse_NoAppFont,
        _ => "",
    };
}

/// <summary>
/// 찾아 바꾸기 패널 모델(0.4.0 Task 6). 찾기(<see cref="FindReplaceService.FindAsync"/>)와 계획(PlanAsync)은 백그라운드, 바꾸기는 명령 한 개(실행 취소 한 번).
/// "모두 바꾸기"는 미리보기가 없으면 먼저 찾기만 하고 멈춘다(U4) — 목록에서 체크한 곳만 바뀐다. 모든 공개 멤버는 UI 스레드에서 쓴다.
/// </summary>
public sealed partial class FindReplaceViewModel : ObservableObject, IDisposable
{
    /// <summary>바꿀 글을 고치면 이 시간(ms) 뒤에 미리보기의 글꼴 상태를 다시 계산한다(시험은 0).</summary>
    public static int PlanDelayMs { get; set; } = 250;

    private readonly DocumentTabViewModel _tab;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _planCts;
    private FindOutcome? _outcome;
    private ReplaceBatch? _batch;
    private long _foundVersion = -1;
    private int _foundPageCount;
    private string _foundWith = "";

    public FindReplaceViewModel(DocumentTabViewModel tab)
    {
        _tab = tab;
        FindCommand = new AsyncRelayCommand(() => FindAsync(false), () => !IsBusy);
        NextCommand = new AsyncRelayCommand(NextAsync, () => !IsBusy);
        ReplaceCommand = new AsyncRelayCommand(ReplaceCurrentAsync, () => !IsBusy);
        ReplaceAllCommand = new AsyncRelayCommand(ReplaceAllAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        CheckAllCommand = new RelayCommand(() => SetAllChecked(true));
        UncheckAllCommand = new RelayCommand(() => SetAllChecked(false));
    }

    // ---- 입력 ----
    [ObservableProperty] private string _findText = "";
    [ObservableProperty] private string _replaceText = "";
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _wholeWord;
    [ObservableProperty] private bool _idWordChars = true;
    [ObservableProperty] private bool _includeRotated = true;
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsScopeAll), nameof(IsScopeCurrent), nameof(IsScopeRange))]
    private ReplaceScope _scope = ReplaceScope.All;

    public bool IsScopeAll { get => Scope == ReplaceScope.All; set { if (value) Scope = ReplaceScope.All; } }
    public bool IsScopeCurrent { get => Scope == ReplaceScope.CurrentPage; set { if (value) Scope = ReplaceScope.CurrentPage; } }
    public bool IsScopeRange { get => Scope == ReplaceScope.Range; set { if (value) Scope = ReplaceScope.Range; } }

    // ---- 상태 ----
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNotice))] private string _noticeText = "";
    public bool HasNotice => NoticeText.Length > 0;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSummary))] private string _summaryText = "";
    public bool HasSummary => SummaryText.Length > 0;
    [ObservableProperty] private int _progressMax = 1;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanReplace))]
    private bool _isBusy;
    [ObservableProperty] private ReplaceRow? _selectedRow;

    public ObservableCollection<ReplaceRow> Rows { get; } = [];
    public IAsyncRelayCommand FindCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand ReplaceCommand { get; }
    public IAsyncRelayCommand ReplaceAllCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand CheckAllCommand { get; }
    public IRelayCommand UncheckAllCommand { get; }

    /// <summary>패널을 닫아 달라는 요청(Esc). 창이 처리한다.</summary>
    public event Action? CloseRequested;
    /// <summary>미리보기 목록·체크·선택이 바뀌어 문서 화면의 강조를 다시 그려야 한다.</summary>
    public event Action? HighlightsChanged;
    public void RequestClose() => CloseRequested?.Invoke();

    /// <summary>바꿀 수 없는 문서면 그 이유(찾기만 된다). 없으면 null.</summary>
    public string? BlockedReason => !_tab.CanEditContent ? Strings.Replace_NoPermission : !_tab.CanUseAppFontText ? Strings.Replace_Encrypted : null;
    public bool HasBlockedReason => BlockedReason is not null;
    /// <summary>바꾸기 단추가 켜지는 조건: 문서가 열려 있고 바꿀 수 있고 바꿀 글이 있다.</summary>
    public bool CanReplace => !IsBusy && _tab.Session is not null && BlockedReason is null && ReplaceText.Length > 0;

    partial void OnIsBusyChanged(bool value)
    {
        FindCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        ReplaceCommand.NotifyCanExecuteChanged();
        ReplaceAllCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnReplaceTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanReplace));
        if (_outcome is not null && !IsBusy) _ = ReplanAsync();
    }

    partial void OnSelectedRowChanged(ReplaceRow? value)
    {
        HighlightsChanged?.Invoke();
        if (value is null) return;
        var b = value.Item.Match.Bounds;
        _tab.RequestReveal(value.Hit.Page, new PdfRectF((float)b.L, (float)b.B, (float)b.R, (float)b.T));
        if (_tab.Session is { } s)
        {
            var pages = s.Pages;   // IndexOf는 PDFium 스레드 전용이라 쪽 목록(불변)에서 찾는다
            for (int i = 0; i < pages.Length; i++)
                if (pages[i].Id == value.Hit.Page) { _tab.CurrentPageIndex = i; break; }
        }
    }

    /// <summary>문서 화면이 읽는 강조 대상(쪽별로 묶어 그린다).</summary>
    internal IEnumerable<ReplaceRow> HighlightRows => Rows;

    /// <summary>결과·강조를 지우고 쓰던 파일을 놓는다(진행 중이면 먼저 취소).</summary>
    public void Clear()
    {
        _cts?.Cancel();
        _planCts?.Cancel();
        DropResults();
        StatusText = "";
        NoticeText = "";
        SummaryText = "";
    }

    public void Dispose() => Clear();

    private void DropResults()
    {
        _batch = null;
        _outcome?.Dispose();
        _outcome = null;
        SelectedRow = null;
        foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        HighlightsChanged?.Invoke();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ReplaceRow.IsChecked)) return;
        UpdateFoundText();
        HighlightsChanged?.Invoke();
    }

    private void SetAllChecked(bool on)
    {
        foreach (var r in Rows) if (r.Ok) r.IsChecked = on;
    }

    // ---- 찾기 ----

    private FindOptions BuildOptions() => new()
    {
        Query = FindText,
        MatchCase = MatchCase,
        WholeWord = WholeWord,
        DrawingIdWordChars = IdWordChars,
        IncludeRotated = IncludeRotated,
    };

    /// <summary>범위를 쪽 번호로 바꾼다. 잘못된 입력이면 false.</summary>
    private bool TryPages(DocumentSession s, out IReadOnlyList<PageId>? pages)
    {
        pages = null;
        var all = s.Pages;
        switch (Scope)
        {
            case ReplaceScope.CurrentPage:
                if (all.Length == 0) return false;
                pages = [all[Math.Clamp(_tab.CurrentPageIndex, 0, all.Length - 1)].Id];
                return true;
            case ReplaceScope.Range:
                if (string.IsNullOrWhiteSpace(RangeText) || !PageRangeParser.TryParse(RangeText, all.Length, out var idx)) return false;
                pages = [.. idx.Select(i => all[i].Id)];
                return true;
            default:
                return true;
        }
    }

    private async Task NextAsync()
    {
        if (Rows.Count == 0) { await FindAsync(false); if (Rows.Count > 0) SelectedRow = Rows[0]; return; }
        int i = SelectedRow is null ? -1 : Rows.IndexOf(SelectedRow);
        SelectedRow = Rows[(i + 1) % Rows.Count];
    }

    /// <param name="keepSummary">바꾼 뒤 목록을 새로 만들 때 요약 줄을 지우지 않는다.</param>
    private async Task FindAsync(bool keepSummary)
    {
        if (IsBusy || _tab.Session is not { } s || string.IsNullOrEmpty(FindText)) return;
        if (!TryPages(s, out var pages)) { StatusText = Strings.Replace_InvalidRange; return; }

        _planCts?.Cancel();
        DropResults();
        if (!keepSummary) SummaryText = "";
        NoticeText = "";
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        int total = pages?.Count ?? s.Pages.Length;
        ProgressMax = Math.Max(1, total);
        ProgressValue = 0;
        StatusText = string.Format(Strings.Replace_Progress, total, 0);
        var progress = new Progress<int>(done =>
        {
            if (cts.IsCancellationRequested) return;
            ProgressValue = done;
            StatusText = string.Format(Strings.Replace_Progress, total, done);
        });
        try
        {
            var outcome = await FindReplaceService.FindAsync(s, BuildOptions(), pages, progress, cts.Token);
            _outcome = outcome;
            _foundVersion = s.Commands.Version;
            _foundPageCount = s.Pages.Length;
            await PlanAndShowAsync(s, outcome, cts.Token);
        }
        catch (OperationCanceledException) { DropResults(); StatusText = ""; }
        catch (InvalidOperationException ex) when (ex.Message is "no-file" or "page-structure-changed")
        {
            DropResults();
            StatusText = Strings.Replace_NeedSave;
        }
        catch (Exception)
        {
            DropResults();
            StatusText = Strings.Replace_Failed;
        }
        finally
        {
            IsBusy = false;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    private async Task PlanAndShowAsync(DocumentSession s, FindOutcome outcome, CancellationToken ct, IReadOnlyDictionary<TextMatch, bool>? keep = null)
    {
        string with = ReplaceText;
        var ap = DocumentTabViewModel.Appearance;
        var batch = await FindReplaceService.PlanAsync(s, outcome, [.. outcome.Matches], with, ap?.Font, ap?.Catalog, ct);
        ct.ThrowIfCancellationRequested();
        _batch = batch;
        _foundWith = with;
        foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        foreach (var it in batch.Items)
        {
            var m = it.Match;
            var b = m.Bounds;
            var hit = new SearchHit(m.Page, m.PageIndex, 0, m.Text.Length, m.BeforeContext + m.Text + m.AfterContext,
                [new RectangleF((float)b.L, (float)b.B, (float)(b.R - b.L), (float)(b.T - b.B))]);
            var row = new ReplaceRow(it, hit, with);
            if (keep is not null && it.Ok && keep.TryGetValue(m, out bool was)) row.IsChecked = was;
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        ShowReport(outcome.Report, batch);
        HighlightsChanged?.Invoke();
    }

    private void ShowReport(FindReport report, ReplaceBatch batch)
    {
        var notes = new List<string>();
        if (report.UnsearchableOps > 0)
            notes.Add(string.Format(Strings.Replace_Unsearchable, report.UnsearchableOps, PageList(report.PagesWithUnsearchable)));
        if (report.FailedPages.Count > 0) notes.Add(string.Format(Strings.Replace_FailedPages, PageList(report.FailedPages)));
        if (batch.AppFontCount > 0 && ReplaceText.Length > 0) notes.Add(string.Format(Strings.Replace_AppFontNotice, batch.AppFontCount));
        if (batch.OverlapCount > 0) notes.Add(string.Format(Strings.Replace_OverlapNotice, batch.OverlapCount));
        if (BlockedReason is { } why) notes.Add(why);
        NoticeText = string.Join("\n", notes);
        if (Rows.Count == 0) StatusText = Strings.Replace_NoResults;
        else UpdateFoundText();
    }

    private void UpdateFoundText() =>
        StatusText = Rows.Count == 0 ? Strings.Replace_NoResults : string.Format(Strings.Replace_Found, Rows.Count, Rows.Count(r => r.IsChecked));

    private static string PageList(IReadOnlyList<int> pages)
    {
        const int Max = 12;
        string s = string.Join(", ", pages.Take(Max));
        return pages.Count > Max ? s + ", …" : s;
    }

    /// <summary>바꿀 글을 고쳤을 때 글꼴 상태·겹침을 다시 계산한다(체크는 유지).</summary>
    private async Task ReplanAsync()
    {
        _planCts?.Cancel();
        var cts = _planCts = new CancellationTokenSource();
        try
        {
            if (PlanDelayMs > 0) await Task.Delay(PlanDelayMs, cts.Token);
            if (_outcome is not { } outcome || _tab.Session is not { } s || IsBusy || cts.IsCancellationRequested) return;
            var keep = Rows.ToDictionary(r => r.Item.Match, r => r.IsChecked, ReferenceEqualityComparer.Instance as IEqualityComparer<TextMatch>);
            string selectedText = SelectedRow?.Item.Match.Text ?? "";
            int sel = SelectedRow is null ? -1 : Rows.IndexOf(SelectedRow);
            await PlanAndShowAsync(s, outcome, cts.Token, keep);
            if (sel >= 0 && sel < Rows.Count) SelectedRow = Rows[sel];
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 미리보기만 갱신하지 못한 것 — 바꾸기 때 다시 계산한다 */ }
    }

    // ---- 바꾸기 ----

    /// <summary>현재 줄 하나를 바꾼다. 고른 줄이 없으면 첫 줄을 고르고 멈춘다(두 번째 누름에 바뀐다).</summary>
    private async Task ReplaceCurrentAsync()
    {
        if (!await EnsurePreviewAsync()) return;
        if (SelectedRow is null || !SelectedRow.Ok)
        {
            SelectedRow = Rows.FirstOrDefault(r => r.Ok) ?? Rows.FirstOrDefault();
            return;
        }
        int at = Rows.IndexOf(SelectedRow);
        var target = SelectedRow.Item;
        await RunReplaceAsync(i => ReferenceEquals(i, target), at);
    }

    private async Task ReplaceAllAsync()
    {
        if (!await EnsurePreviewAsync()) return;
        var chosen = Rows.Where(r => r.Ok && r.IsChecked).Select(r => r.Item).ToHashSet();
        if (chosen.Count == 0) return;
        await RunReplaceAsync(i => chosen.Contains(i), -1);
    }

    /// <summary>
    /// 바꾸기 전에 미리보기가 있어야 한다. 없거나 문서가 그 뒤 바뀌었거나 바꿀 글이 달라졌으면 찾기만 하고 false(U4).
    /// </summary>
    private async Task<bool> EnsurePreviewAsync()
    {
        if (_tab.Session is not { } s) return false;
        if (ReplaceText.Length == 0) { StatusText = Strings.Replace_EmptyWith; return false; }
        if (BlockedReason is { } why) { StatusText = why; return false; }
        if (_outcome is not null && (s.Commands.Version != _foundVersion || s.Pages.Length != _foundPageCount))
        {
            DropResults();
            StatusText = Strings.Replace_Stale;
            return false;
        }
        if (_outcome is null || _batch is null)
        {
            await FindAsync(false);
            return false;
        }
        if (_foundWith != ReplaceText)
        {
            _planCts?.Cancel();
            IsBusy = true;
            try
            {
                var keep = Rows.ToDictionary(r => r.Item.Match, r => r.IsChecked, ReferenceEqualityComparer.Instance as IEqualityComparer<TextMatch>);
                await PlanAndShowAsync(s, _outcome, CancellationToken.None, keep);
            }
            catch (Exception) { DropResults(); StatusText = Strings.Replace_Failed; return false; }
            finally { IsBusy = false; }
        }
        return true;
    }

    private async Task RunReplaceAsync(Func<ReplaceItem, bool> include, int selectAt)
    {
        if (_tab.Session is not { } s || _batch is not { } batch) return;
        var chosen = batch.Items.Where(i => i.Ok && include(i)).ToList();
        int appFont = chosen.Count(i => i.Font == ReplaceFontKind.App && i.Plan.NewText.Length > 0);
        int refused = batch.RefusedCount;
        var refusedPages = batch.RefusedPages;
        IsBusy = true;
        StatusText = Strings.Replace_Replacing;
        try
        {
            var cmd = FindReplaceService.BuildCommand(s, batch, include);
            if (cmd is null) { StatusText = Strings.Replace_NoResults; return; }
            await s.Commands.ExecuteAsync(cmd);
            string summary = string.Format(Strings.Replace_Done, chosen.Count);
            if (appFont > 0) summary += string.Format(Strings.Replace_DoneAppFont, appFont);
            if (refused > 0) summary += string.Format(Strings.Replace_DoneRefused, refused, PageList(refusedPages));
            SummaryText = summary;
        }
        catch (Exception)
        {
            StatusText = Strings.Replace_Failed;
            return;
        }
        finally { IsBusy = false; }

        // 바꾼 낱말은 후보에서 빠지므로 목록을 다시 만든다(남은 곳·못 바꾼 곳이 보인다).
        await FindAsync(true);
        if (selectAt >= 0 && Rows.Count > 0) SelectedRow = Rows[Math.Min(selectAt, Rows.Count - 1)];
    }
}
