using System.Collections.ObjectModel;
using System.Drawing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document;

namespace PdfEditor.App.Panels;

/// <param name="Page">쪽 식별자. 쪽을 옮기거나 지워도 따라간다.</param>
/// <param name="PageIndexAtSearch">검색한 순간의 쪽 위치(0부터). 표시용이고 이동에는 Page를 쓴다.</param>
/// <param name="RectsPt">PDF 사용자 좌표(pt)의 글자 사각형들. 화면 좌표 변환은 PageTransform이 한다.</param>
public sealed record SearchHit(PageId Page, int PageIndexAtSearch, int CharIndex, int CharCount, string Context,
    IReadOnlyList<RectangleF> RectsPt)
{
    /// <summary>결과 목록에 보이는 "N쪽" 표시.</summary>
    public string Label => string.Format(Strings.Search_PageLabel, PageIndexAtSearch + 1);
}

/// <summary>
/// 검색 패널 모델. 현재 쪽부터 끝까지, 그다음 처음부터 현재 쪽 앞까지 한 쪽씩 찾는다.
/// 쪽마다 전용 스레드 작업(InvokeAsync)을 한 번만 보내서 그 사이에 타일 렌더가 끼어들 수 있게 한다.
/// 모든 공개 멤버는 UI 스레드에서 쓴다.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private const int ContextChars = 20;
    private readonly DocumentTabViewModel _tab;
    private CancellationTokenSource? _cts;

    public SearchViewModel(DocumentTabViewModel tab)
    {
        _tab = tab;
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsSearching);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsSearching);
        NextCommand = new RelayCommand(() => Step(+1), () => Results.Count > 0);
        PrevCommand = new RelayCommand(() => Step(-1), () => Results.Count > 0);
        Results.CollectionChanged += (_, _) =>
        {
            NextCommand.NotifyCanExecuteChanged();
            PrevCommand.NotifyCanExecuteChanged();
        };
    }

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _wholeWord;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SearchCommand)), NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isSearching;
    [ObservableProperty] private SearchHit? _currentHit;

    public ObservableCollection<SearchHit> Results { get; } = [];
    public IAsyncRelayCommand SearchCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand NextCommand { get; }
    public IRelayCommand PrevCommand { get; }
    /// <summary>검색 패널의 "찾아 바꾸기…" 단추: 찾던 글을 가지고 바꾸기 패널을 연다(창이 처리).</summary>
    public event Action? OpenReplaceRequested;
    public IRelayCommand OpenReplaceCommand => _openReplace ??= new RelayCommand(() => OpenReplaceRequested?.Invoke());
    private IRelayCommand? _openReplace;

    /// <summary>결과와 강조를 지운다(진행 중이면 먼저 취소한다).</summary>
    public void Clear()
    {
        _cts?.Cancel();
        CurrentHit = null;
        Results.Clear();
        StatusText = "";
    }

    private async Task SearchAsync()
    {
        if (IsSearching || _tab.Session is not { } s || string.IsNullOrEmpty(Query)) return;
        string query = Query;
        bool matchCase = MatchCase, wholeWord = WholeWord;

        CurrentHit = null;
        Results.Clear();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        IsSearching = true;
        try
        {
            var pages = s.Pages;
            int n = pages.Length;
            int start = n == 0 ? 0 : Math.Clamp(_tab.CurrentPageIndex, 0, n - 1);
            bool anyText = false;
            int done = 0;
            StatusText = string.Format(Strings.Search_Progress, 0, n, 0);
            for (int k = 0; k < n; k++)
            {
                if (ct.IsCancellationRequested) break;
                int index = (start + k) % n;
                var id = pages[index].Id;
                // 쪽마다 전용 스레드 작업 한 번. 핸들은 이 안에서만 쓰고 밖으로 내보내지 않는다.
                var (hasText, hits) = await s.Thread.InvokeAsync(() => SearchPage(s, id, index, query, matchCase, wholeWord));
                anyText |= hasText;
                done++;
                foreach (var h in hits) Results.Add(h);
                StatusText = string.Format(Strings.Search_Progress, Results.Count, n, done);
            }
            if (!ct.IsCancellationRequested)
            {
                if (!anyText && n > 0) StatusText = Strings.Msg_NoTextLayer;
                else if (Results.Count == 0) StatusText = Strings.Search_NoResults;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsSearching = false;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    /// <summary>전용 스레드에서 한 쪽을 검색한다. 쪽이 그사이 없어졌으면 건너뛴다.</summary>
    private static (bool HasText, List<SearchHit> Hits) SearchPage(DocumentSession s, PageId id, int index,
        string query, bool matchCase, bool wholeWord)
    {
        var list = new List<SearchHit>();
        if (s.IndexOf(id) < 0) return (false, list);
        var text = s.GetLoadedText(id);
        int total = text.CharCount;
        if (total <= 0) return (false, list);
        foreach (var m in text.FindAll(query, matchCase, wholeWord))
        {
            int from = Math.Max(0, m.CharIndex - ContextChars);
            int to = Math.Min(total, m.CharIndex + m.CharCount + ContextChars);
            string ctx = Flatten(text.GetText(from, to - from));
            list.Add(new SearchHit(id, index, m.CharIndex, m.CharCount, ctx, text.GetRects(m.CharIndex, m.CharCount)));
        }
        return (true, list);
    }

    private static string Flatten(string s) =>
        string.Create(s.Length, s, (span, src) =>
        {
            for (int i = 0; i < src.Length; i++) span[i] = char.IsControl(src[i]) ? ' ' : src[i];
        });

    private void Step(int dir)
    {
        if (Results.Count == 0) return;
        int i = CurrentHit is null ? -1 : Results.IndexOf(CurrentHit);
        int next = i < 0 ? (dir > 0 ? 0 : Results.Count - 1) : (i + dir + Results.Count) % Results.Count;
        CurrentHit = Results[next];
    }

    // 현재 결과가 바뀌면(다음/이전 또는 목록 선택) 그 쪽으로 이동한다. 강조는 문서 화면이 CurrentHit을 읽어 그린다.
    partial void OnCurrentHitChanged(SearchHit? value)
    {
        if (value is null || _tab.Session is not { } s) return;
        var pages = s.Pages;
        for (int i = 0; i < pages.Length; i++)
            if (pages[i].Id == value.Page) { _tab.CurrentPageIndex = i; return; }
    }
}
