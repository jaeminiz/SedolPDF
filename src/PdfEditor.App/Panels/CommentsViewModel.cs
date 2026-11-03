using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Pages;

namespace PdfEditor.App.Panels;

public enum CommentSort { Page, Date, Author, Kind }

/// <summary>
/// 코멘트 목록 모델 (M3b B2·B3). 문서의 모든 주석을 한 목록으로 보여 준다: 편집 가능한 주석(스냅숏) + 읽기 전용 원본(회색, 이동만).
/// 답글은 부모 아래 들여쓰고(Depth 1), 텍스트 바꾸기 묶음은 한 줄, 검토 상태 주석·Popup은 줄로 나오지 않는다(상태는 부모 줄의 State).
/// 쪽마다 줄 묶음을 캐시하고, 주석이 바뀐 쪽만 다시 만든다. 거르기·정렬은 캐시에서 메모리로만 한다.
/// 거르기: 종류·상태·쪽 범위는 부모(맨 윗줄) 기준, 작성자·글자 검색은 부모나 답글 중 하나라도 맞으면 그 묶음 전체를 보인다.
/// 스레드: 공개 멤버는 UI 스레드 전용이다. 주석 저장소 이벤트는 PDFium 전용 스레드에서 오므로 UI로 넘긴 뒤 컬렉션을 만진다.
/// </summary>
public sealed partial class CommentsViewModel : ObservableObject, IDisposable
{
    /// <summary>묶음 하나 = 맨 윗줄(Root) + 그 아래 답글. 읽기 전용 원본은 Root만 있다.</summary>
    private sealed class Group(CommentItem root, List<CommentItem> replies, int seq)
    {
        public CommentItem Root { get; } = root;
        public List<CommentItem> Replies { get; } = replies;
        /// <summary>쪽 안에서 만든 순서 (정렬 동점 처리).</summary>
        public int Seq { get; } = seq;
    }

    /// <summary>한 번의 알림으로 통째로 바꾸는 컬렉션 (1,000줄을 하나씩 넣으며 알림을 1,000번 보내지 않는다).</summary>
    private sealed class BulkCollection : ObservableCollection<CommentItem>
    {
        public void ReplaceAll(IReadOnlyList<CommentItem> next)
        {
            Items.Clear();
            foreach (var i in next) Items.Add(i);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    private static readonly IReadOnlySet<string> NoKinds = new HashSet<string>();

    private readonly DocumentTabViewModel _tab;
    private readonly Dictionary<PageId, List<Group>> _cache = [];
    private readonly Dictionary<PageId, int> _rebuildCounts = [];
    private readonly BulkCollection _items = [];
    private IReadOnlySet<int>? _pageSet;   // null = 모든 쪽 (1부터 센 쪽 번호)
    private bool _disposed;

    public CommentsViewModel(DocumentTabViewModel tab)
    {
        _tab = tab;
        GoToCommand = new RelayCommand<CommentItem>(i => { if (i is not null) GoTo(i); });
        ReplyCommand = new RelayCommand<CommentItem>(BeginReply, i => CanReply && i is { Editable: true });
        DeleteCommand = new AsyncRelayCommand<CommentItem>(i => i is null ? Task.CompletedTask : DeleteSafeAsync(i), i => CanDelete(i));
        if (tab.Session is { } s)
        {
            s.Annotations.Changed += OnStoreChanged;
            s.PagesChanged += OnPagesChanged;
            RebuildAll();
        }
        Refresh();
    }

    /// <summary>한 쪽의 줄 묶음(거르기·정렬 없이, 쪽 안 순서). 주석 요약 PDF가 같은 줄 구성을 쓴다. UI 스레드 전용.</summary>
    internal IReadOnlyList<(CommentItem Root, IReadOnlyList<CommentItem> Replies)> GroupsOfPage(PageId id)
        => _cache.TryGetValue(id, out var gs) ? gs.Select(g => (g.Root, (IReadOnlyList<CommentItem>)g.Replies)).ToList() : [];

    /// <summary>거르기·정렬이 반영된 줄들. 목록 화면이 이것을 보여 준다.</summary>
    public ObservableCollection<CommentItem> Items => _items;

    // ---- 거르기 (비어 있으면 모두) ----
    [ObservableProperty] private IReadOnlySet<string> _kindFilter = NoKinds;
    [ObservableProperty] private string? _authorFilter;
    [ObservableProperty] private ReviewState? _stateFilter;
    [ObservableProperty] private string _pageRange = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private CommentSort _sortBy = CommentSort.Page;
    [ObservableProperty] private bool _sortDescending;

    [ObservableProperty] private bool _pageRangeValid = true;
    [ObservableProperty] private IReadOnlyList<string> _authors = [];
    [ObservableProperty] private IReadOnlyList<string> _kinds = [];
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _shownCount;

    public ICommand GoToCommand { get; }

    // ---- 화면의 거르기·정렬 선택 칸 (첫 항목 = 모두) ----
    public IReadOnlyList<string> SortChoices { get; } = [Strings.Comment_SortPage, Strings.Comment_SortDate, Strings.Comment_SortAuthor, Strings.Comment_SortKind];
    public IReadOnlyList<string> KindChoices => [Strings.Comment_AllKinds, .. Kinds];
    public IReadOnlyList<string> AuthorChoices => [Strings.Comment_AllAuthors, .. Authors];
    public IReadOnlyList<string> StateChoices { get; } =
        [Strings.Comment_AllStates, .. Enum.GetValues<ReviewState>().Select(StateName)];

    public int SortIndex
    {
        get => (int)SortBy;
        set { if (value >= 0 && value <= (int)CommentSort.Kind) SortBy = (CommentSort)value; }
    }

    public int KindIndex
    {
        get => KindFilter.Count == 1 ? Math.Max(0, 1 + IndexOf(Kinds, KindFilter.First())) : 0;
        set => KindFilter = value <= 0 || value > Kinds.Count ? NoKinds : new HashSet<string> { Kinds[value - 1] };
    }

    public int AuthorIndex
    {
        get => string.IsNullOrEmpty(AuthorFilter) ? 0 : Math.Max(0, 1 + IndexOf(Authors, AuthorFilter));
        set => AuthorFilter = value <= 0 || value > Authors.Count ? null : Authors[value - 1];
    }

    public int StateIndex
    {
        get => StateFilter is { } s ? 1 + (int)s : 0;
        set => StateFilter = value <= 0 ? null : (ReviewState)(value - 1);
    }

    private static int IndexOf(IReadOnlyList<string> list, string v)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == v) return i;
        return -1;
    }

    partial void OnKindsChanged(IReadOnlyList<string> value) { OnPropertyChanged(nameof(KindChoices)); OnPropertyChanged(nameof(KindIndex)); }
    partial void OnAuthorsChanged(IReadOnlyList<string> value) { OnPropertyChanged(nameof(AuthorChoices)); OnPropertyChanged(nameof(AuthorIndex)); }

    public string CountText => string.Format(Strings.Comment_Count, ShownCount, TotalCount);
    public bool IsEmptyList => ShownCount == 0;
    partial void OnShownCountChanged(int value) { OnPropertyChanged(nameof(CountText)); OnPropertyChanged(nameof(IsEmptyList)); }
    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(CountText));

    /// <summary>답글 입력칸을 연다 (답글을 달 수 없는 문서에서는 실행 불가).</summary>
    public IRelayCommand<CommentItem> ReplyCommand { get; }
    public IAsyncRelayCommand<CommentItem> DeleteCommand { get; }

    /// <summary>답글·검토 상태를 달 수 있는가: 주석 권한이 있고 저장 때 PDFsharp 패치가 되는 문서 (암호 문서는 불가, M3a A2).</summary>
    public bool CanReply => _tab.Session is not null && _tab.CanAnnotate && _tab.CanUsePatchFeatures;

    /// <summary>답글·상태를 달 수 없는 이유 (달 수 있거나 주석 권한 자체가 없으면 null — 그 경우는 탭의 안내가 알린다).</summary>
    public string? ReplyNotice => _tab.Session is not null && _tab.CanAnnotate && !_tab.CanUsePatchFeatures
        ? Strings.Msg_RepliesUnavailableEncrypted : null;

    public bool HasReplyNotice => ReplyNotice is not null;

    public static string StateName(ReviewState s) => s switch
    {
        ReviewState.Accepted => Strings.State_Accepted,
        ReviewState.Rejected => Strings.State_Rejected,
        ReviewState.Cancelled => Strings.State_Cancelled,
        ReviewState.Completed => Strings.State_Completed,
        _ => Strings.State_None,
    };

    public bool CanDelete(CommentItem? i) => i is { Editable: true, Id: not null } && _tab.CanAnnotate;

    // ---- 답글·상태·삭제 (명령 하나씩, 작성자 = 설정의 작성자 이름) ----

    /// <summary>parent 아래에 답글을 단다. parent가 이미 답글이 달린 주석이면 스레드 끝에 붙는다 (시각순).</summary>
    public async Task ReplyAsync(CommentItem parent, string text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0 || !CanReply || !parent.Editable) return;
        if (!Find(parent, out var s, out var snap, out var target)) return;
        var reply = ThreadAnnotations.NewReply(s.Annotations, target, text, DocumentTabViewModel.CurrentAuthor, NextStamp(snap, target));
        await _tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Add(parent.Page, reply));
    }

    /// <summary>스레드 뿌리의 검토 상태를 정한다 (줄이 답글이면 그 스레드의 뿌리에). 이미 그 상태면 아무것도 하지 않는다.</summary>
    public async Task SetStateAsync(CommentItem root, ReviewState state)
    {
        if (!CanReply || !root.Editable) return;
        if (!Find(root, out var s, out var snap, out var target)) return;
        var top = ThreadRoot(snap, target);
        if (snap.CurrentState(top.Id) == state) return;
        var st = ThreadAnnotations.NewState(s.Annotations, top, state, DocumentTabViewModel.CurrentAuthor, NextStamp(snap, top),
            Strings.Comment_StateTemplate, StateName(state));
        await _tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Add(root.Page, st));
    }

    /// <summary>줄의 주석을 지운다 — 답글·상태·바꾸기 묶음 구성원까지 한 명령으로 (부모만 지우면 답글이 고아가 된다).</summary>
    public async Task DeleteAsync(CommentItem item)
    {
        if (!CanDelete(item) || _tab.Session is not { } s) return;
        var set = s.Annotations.DeleteSetOf(item.Page, item.Id!.Value);
        if (set.Count == 0) return;
        await _tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.DeleteThread(item.Page, set));
    }

    /// <summary>입력칸에서 확정: 쓴 글로 답글을 달고 칸을 닫는다.</summary>
    /// <param name="currentText">입력칸이 지금 가진 글. 한글 조합 중인 마지막 글자처럼 Draft 연결이 아직 못 받은 글이 있을 수 있어 화면이 직접 넘긴다.</param>
    public async Task CommitReplyAsync(CommentItem item, string? currentText = null)
    {
        if (currentText is not null) item.Ui.Draft = currentText;
        string text = item.Ui.Draft;
        if (string.IsNullOrWhiteSpace(text)) { CancelReply(item); return; }
        // 답글이 달리면 목록이 다시 만들어지는데, 그때 이 줄의 임시 글(_drafts)이 남아 있으면 새 줄에서 입력칸이 다시 열린다 — 먼저 지운다.
        if (item.Id is { } id) _drafts.Remove(id);
        try { await ReplyAsync(item, text); }
        catch (Exception ex)
        {
            if (item.Id is { } rid && item.Ui.IsReplying) _drafts[rid] = item.Ui.Draft;
            _tab.RaiseNotice(PdfEditor.App.Documents.ErrorMessages.For(ex));
            return;
        }
        item.Ui.Draft = "";
        item.Ui.IsReplying = false;
    }

    /// <summary>열려 있는 답글 입력칸과 쓰던 글 (주석 Id 기준). 목록을 다시 만들어도(쪽 구조 변경·다른 쪽 편집) 그대로 이어진다.</summary>
    private readonly Dictionary<AnnotId, string> _drafts = [];

    private CommentItem BindUi(CommentItem item)
    {
        if (item.Id is not { } id) return item;
        if (_drafts.TryGetValue(id, out var draft))
        {
            item.Ui.Draft = draft;
            item.Ui.IsReplying = true;
        }
        item.Ui.PropertyChanged += (_, e) =>
        {
            if (_disposed) return;
            if (e.PropertyName == nameof(CommentRowUi.IsReplying))
            {
                if (item.Ui.IsReplying) _drafts[id] = item.Ui.Draft; else _drafts.Remove(id);
            }
            else if (e.PropertyName == nameof(CommentRowUi.Draft) && item.Ui.IsReplying) _drafts[id] = item.Ui.Draft;
        };
        return item;
    }

    /// <summary>테스트용: 이 주석에 열려 있는 입력칸의 쓰던 글 (없으면 null).</summary>
    public string? DraftFor(AnnotId id) => _drafts.TryGetValue(id, out var d) ? d : null;

    public void CancelReply(CommentItem item)
    {
        item.Ui.IsReplying = false;
        item.Ui.Draft = "";
    }

    private void BeginReply(CommentItem? item)
    {
        if (item is null || !CanReply || !item.Editable) return;
        foreach (var other in _items)
            if (!ReferenceEquals(other, item) && other.Ui.IsReplying) other.Ui.IsReplying = false;   // 한 번에 하나
        item.Ui.IsReplying = true;
    }

    private async Task DeleteSafeAsync(CommentItem item)
    {
        try { await DeleteAsync(item); }
        catch (Exception ex) { _tab.RaiseNotice(PdfEditor.App.Documents.ErrorMessages.For(ex)); }
    }

    /// <summary>상태 바꾸기를 화면에서 부를 때 (오류는 안내로).</summary>
    public async Task SetStateSafeAsync(CommentItem item, ReviewState state)
    {
        try { await SetStateAsync(item, state); }
        catch (Exception ex) { _tab.RaiseNotice(PdfEditor.App.Documents.ErrorMessages.For(ex)); }
    }

    private bool Find(CommentItem item, out PdfEditor.Document.DocumentSession s, out PageAnnotations snap, out Annotation target)
    {
        s = _tab.Session!;
        snap = default!;
        target = default!;
        if (_tab.Session is null || item.Id is not { } id) return false;
        snap = s.Annotations.Get(item.Page);
        var found = snap.Items.FirstOrDefault(v => v.Data.Id == id)?.Data;
        if (found is null) return false;
        target = found;
        return true;
    }

    private static Annotation ThreadRoot(PageAnnotations snap, Annotation a)
    {
        var byId = snap.Items.ToDictionary(v => v.Data.Id, v => v.Data);
        var seen = new HashSet<AnnotId>();
        var cur = a;
        while (cur.IsThreadMember() && cur.InReplyTo is { } p && seen.Add(cur.Id) && byId.TryGetValue(p, out var up)) cur = up;
        return cur;
    }

    /// <summary>지금 시각. 단, 스레드에서 가장 늦은 시각보다 반드시 뒤여야 한다 (시각순 표시에서 스레드 끝에 오도록).</summary>
    private static DateTimeOffset NextStamp(PageAnnotations snap, Annotation target)
    {
        var now = DateTimeOffset.Now;
        var latest = ThreadRoot(snap, target) is { } top ? snap.ThreadOf(top.Id).Select(a => a.ModifiedAt ?? a.CreatedAt ?? DateTimeOffset.MinValue).DefaultIfEmpty().Max() : default;
        return latest >= now ? latest.AddMilliseconds(1) : now;
    }

    /// <summary>테스트용: 이 쪽의 줄 묶음을 다시 만든 횟수.</summary>
    public int RebuildCount(PageId page) => _rebuildCounts.GetValueOrDefault(page);

    partial void OnKindFilterChanged(IReadOnlySet<string> value) { OnPropertyChanged(nameof(KindIndex)); Refresh(); }
    partial void OnAuthorFilterChanged(string? value) { OnPropertyChanged(nameof(AuthorIndex)); Refresh(); }
    partial void OnStateFilterChanged(ReviewState? value) { OnPropertyChanged(nameof(StateIndex)); Refresh(); }
    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnSortByChanged(CommentSort value) { OnPropertyChanged(nameof(SortIndex)); Refresh(); }
    partial void OnSortDescendingChanged(bool value) => Refresh();
    partial void OnPageRangeChanged(string value)
    {
        ParsePageRange();
        Refresh();
    }

    // ---- 이동 ----

    /// <summary>
    /// 그 쪽으로 이동하고 사각형이 보이게 한다. 편집 가능한 줄이면 주석(바꾸기 묶음은 구성원 모두)을 선택한다.
    /// 읽기 전용 줄은 이동만 하고 선택은 건드리지 않는다.
    /// </summary>
    public void GoTo(CommentItem item)
    {
        if (_tab.Session is not { } s) return;
        var pages = s.Pages;
        int idx = -1;
        for (int i = 0; i < pages.Length; i++)
            if (pages[i].Id == item.Page) { idx = i; break; }
        if (idx < 0) return;

        _tab.CurrentPageIndex = idx;
        if (item.Editable && item.Id is { } id)
        {
            if (_tab.Tool != ToolKind.AnnotSelect) _tab.Tool = ToolKind.AnnotSelect;   // 도구를 바꾸면 선택이 비워지므로 먼저 바꾼다
            var members = s.Annotations.GroupOf(item.Page, id);
            _tab.Selection.Set(item.Page, members.Count > 0 ? [.. members.Select(m => m.Id)] : [id]);
        }
        _tab.RequestReveal(item.Page, item.Rect);
    }

    // ---- 갱신 ----

    private void OnStoreChanged(AnnotationsChangedEventArgs e) => _tab.PostToUi(() =>
    {
        if (_disposed) return;
        RebuildPages(e.Pages);
        Refresh();
    });

    private void OnPagesChanged(PagesChangedEventArgs e)
    {
        if (!e.StructureChanged) return;   // 크기·회전만 바뀐 것은 목록과 무관 (쪽 번호는 구조가 바뀔 때만 달라진다)
        _tab.PostToUi(() =>
        {
            if (_disposed) return;
            RebuildAll();
            Refresh();
        });
    }

    private void RebuildAll()
    {
        _cache.Clear();
        if (_tab.Session is not { } s) return;
        var numbers = PageNumbers(s);
        foreach (var (id, number) in numbers) BuildPage(s, id, number);
        ParsePageRange();
        UpdateLists();
    }

    private void RebuildPages(IReadOnlyList<PageId> pages)
    {
        if (_tab.Session is not { } s) return;
        var numbers = PageNumbers(s);
        foreach (var id in pages)
        {
            if (numbers.TryGetValue(id, out int number)) BuildPage(s, id, number);
            else _cache.Remove(id);   // 지워진 쪽
        }
        UpdateLists();
    }

    private static Dictionary<PageId, int> PageNumbers(PdfEditor.Document.DocumentSession s)
    {
        var pages = s.Pages;
        var map = new Dictionary<PageId, int>(pages.Length);
        for (int i = 0; i < pages.Length; i++) map[pages[i].Id] = i + 1;
        return map;
    }

    private void ParsePageRange()
    {
        int count = _tab.Session?.Pages.Length ?? 0;
        if (string.IsNullOrWhiteSpace(PageRange)) { _pageSet = null; PageRangeValid = true; return; }
        if (PageRangeParser.TryParse(PageRange, count, out var idx))
        {
            _pageSet = idx.Select(i => i + 1).ToHashSet();
            PageRangeValid = true;
        }
        else
        {
            _pageSet = null;   // 잘못 입력한 동안에는 쪽 거르기를 하지 않는다
            PageRangeValid = false;
        }
    }

    private void BuildPage(PdfEditor.Document.DocumentSession s, PageId id, int number)
    {
        _rebuildCounts[id] = _rebuildCounts.GetValueOrDefault(id) + 1;
        var snap = s.Annotations.Get(id);
        var groups = new List<Group>(snap.Items.Length + snap.ReadOnly.Length);
        int seq = 0;

        var byId = new Dictionary<AnnotId, Annotation>(snap.Items.Length);
        foreach (var v in snap.Items) byId[v.Data.Id] = v.Data;
        Annotation? Parent(Annotation a) => a.InReplyTo is { } p && byId.TryGetValue(p, out var x) ? x : null;
        bool IsGroupMember(Annotation a) => a.ReplyKind == ReplyType.Group && Parent(a) is not null;
        Annotation RootOf(Annotation a)
        {
            var cur = a;
            var seen = new HashSet<AnnotId>();
            while (cur.IsThreadMember() && seen.Add(cur.Id) && Parent(cur) is { } p) cur = p;
            if (IsGroupMember(cur)) cur = Parent(cur)!;
            return cur;
        }

        // 바꾸기 묶음 구성원과 검토 상태가 달린 부모
        var groupMembers = new Dictionary<AnnotId, List<Annotation>>();
        var withState = new HashSet<AnnotId>();
        foreach (var v in snap.Items)
        {
            var a = v.Data;
            if (IsGroupMember(a)) (groupMembers.TryGetValue(a.InReplyTo!.Value, out var l) ? l : groupMembers[a.InReplyTo.Value] = []).Add(a);
            if (a.State is not null && a.InReplyTo is { } sp) withState.Add(sp);
        }

        var byRoot = new Dictionary<AnnotId, Group>();
        foreach (var v in snap.Items)
        {
            var a = v.Data;
            if (a.State is not null && a.IsThreadMember()) continue;   // 상태 주석은 줄이 아니다
            if (IsGroupMember(a)) continue;                              // 묶음 구성원은 대표 줄에 들어간다
            if (RootOf(a) != a) continue;                                // 답글은 아래에서
            groupMembers.TryGetValue(a.Id, out var members);
            var g = new Group(BindUi(MakeItem(snap, id, number, a, members, withState, depth: 0)), [], seq++);
            groups.Add(g);
            byRoot[a.Id] = g;
        }
        foreach (var v in snap.Items)
        {
            var a = v.Data;
            if (a.State is not null || !a.IsThreadMember() || IsGroupMember(a)) continue;
            var root = RootOf(a);
            if (root == a || !byRoot.TryGetValue(root.Id, out var g)) continue;
            g.Replies.Add(BindUi(MakeItem(snap, id, number, a, null, withState, depth: 1)));
        }
        foreach (var g in groups) StableSortReplies(g.Replies);

        foreach (var ro in snap.ReadOnly)
        {
            var item = new CommentItem(id, number, null, ro.Index, ReadOnlyKindName(ro.Subtype), ro.Author, ro.Contents, ro.ModifiedAt,
                ReviewState.None, 0, false, ro.Rect, AnnotKindExtensions.FromPdfSubtype(ro.Subtype) is { } rk ? IconFor(new Annotation { Kind = rk }, false) : "generic") { ReadOnlySubtype = ro.Subtype };
            groups.Add(new Group(item, [], seq++));
        }
        _cache[id] = groups;
    }

    private static void StableSortReplies(List<CommentItem> replies)
    {
        if (replies.Count < 2) return;
        var sorted = replies.Select((r, i) => (r, i)).OrderBy(t => t.r.When ?? DateTimeOffset.MinValue).ThenBy(t => t.i).Select(t => t.r).ToList();
        replies.Clear();
        replies.AddRange(sorted);
    }

    private static CommentItem MakeItem(PageAnnotations snap, PageId page, int number, Annotation a, List<Annotation>? members,
        HashSet<AnnotId> withState, int depth)
    {
        bool replace = a.Kind == AnnotKind.StrikeOut && members is { Count: > 0 } && members.Any(m => m.Kind == AnnotKind.Caret);
        string? contents = a.Contents;
        if (replace)
            contents = members!.Select(m => m.Contents).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? a.Contents;
        var state = withState.Contains(a.Id) ? snap.CurrentState(a.Id) : ReviewState.None;
        return new CommentItem(page, number, a.Id, null, KindName(a, replace), a.Author, contents, a.ModifiedAt ?? a.CreatedAt,
            state, depth, true, a.Rect, IconFor(a, replace));
    }

    // ---- 종류 이름 (화면 문구는 Strings 리소스) ----

    private static string KindName(Annotation a, bool replaceGroup)
    {
        if (replaceGroup) return Strings.AnnotKind_ReplaceText;
        return a.Kind switch
        {
            AnnotKind.Ink => Strings.AnnotKind_Ink,
            AnnotKind.Highlight => Strings.AnnotKind_Highlight,
            AnnotKind.Underline => Strings.AnnotKind_Underline,
            AnnotKind.StrikeOut => Strings.AnnotKind_StrikeOut,
            AnnotKind.Text => a.IsThreadMember() ? Strings.AnnotKind_Reply : Strings.AnnotKind_Text,
            AnnotKind.Circle => Strings.AnnotKind_Circle,
            AnnotKind.Square => a.CloudIntensity > 0f ? Strings.AnnotKind_Cloud : Strings.AnnotKind_Square,
            AnnotKind.Polygon => a.CloudIntensity > 0f ? Strings.AnnotKind_Cloud : Strings.AnnotKind_Polygon,
            AnnotKind.PolyLine => Strings.AnnotKind_PolyLine,
            AnnotKind.Line => IsArrow(a.StartEnding) || IsArrow(a.EndEnding) ? Strings.AnnotKind_Arrow : Strings.AnnotKind_Line,
            AnnotKind.FreeText => a.Intent switch
            {
                FreeTextIntent.Typewriter => Strings.AnnotKind_Typewriter,
                FreeTextIntent.Callout => Strings.AnnotKind_Callout,
                _ => Strings.AnnotKind_FreeText,
            },
            AnnotKind.Caret => Strings.AnnotKind_Caret,
            AnnotKind.Stamp => Strings.AnnotKind_Stamp,
            AnnotKind.FileAttachment => Strings.AnnotKind_FileAttachment,
            _ => Strings.AnnotKind_Other,
        };
    }

    /// <summary>줄 앞에 그리는 종류 아이콘 (Icons.axaml 키).</summary>
    private static string IconFor(Annotation a, bool replaceGroup)
    {
        if (replaceGroup) return "replace";
        return a.Kind switch
        {
            AnnotKind.Ink => "pen",
            AnnotKind.Highlight => "hl",
            AnnotKind.Underline => "under",
            AnnotKind.StrikeOut => "strike",
            AnnotKind.Text => a.IsThreadMember() ? "comments" : "note",
            AnnotKind.Circle => "oval",
            AnnotKind.Square => a.CloudIntensity > 0f ? "cloud" : "rect",
            AnnotKind.Polygon => a.CloudIntensity > 0f ? "cloud" : "poly",
            AnnotKind.PolyLine => "pline",
            AnnotKind.Line => IsArrow(a.StartEnding) || IsArrow(a.EndEnding) ? "arrow" : "line",
            AnnotKind.FreeText => a.Intent == FreeTextIntent.Callout ? "callout" : "type",
            AnnotKind.Caret => "caret",
            AnnotKind.Stamp => "stamp",
            AnnotKind.FileAttachment => "clip",
            _ => "generic",
        };
    }

    private static bool IsArrow(LineEnding e) => e is LineEnding.OpenArrow or LineEnding.ClosedArrow;

    private static string ReadOnlyKindName(int subtype) => subtype switch
    {
        PdfAnnotSubtype.Link => Strings.AnnotKind_Link,
        PdfAnnotSubtype.Widget => Strings.AnnotKind_Widget,
        PdfAnnotSubtype.Squiggly => Strings.AnnotKind_Squiggly,
        PdfAnnotSubtype.Stamp => Strings.AnnotKind_Stamp,
        PdfAnnotSubtype.FileAttachment => Strings.AnnotKind_FileAttachment,
        _ => AnnotKindExtensions.FromPdfSubtype(subtype) is { } k
            ? KindName(new Annotation { Kind = k }, false)
            : Strings.AnnotKind_Other,
    };

    // ---- 거르기·정렬 ----

    /// <summary>작성자·종류 목록과 전체 개수 (주석이 바뀌었을 때만 다시 센다).</summary>
    private void UpdateLists()
    {
        var authors = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var kinds = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        int total = 0;
        foreach (var groups in _cache.Values)
            foreach (var g in groups)
            {
                total += 1 + g.Replies.Count;
                kinds.Add(g.Root.KindName);
                if (!string.IsNullOrEmpty(g.Root.Author)) authors.Add(g.Root.Author);
                foreach (var r in g.Replies)
                    if (!string.IsNullOrEmpty(r.Author)) authors.Add(r.Author);
            }
        if (!authors.SequenceEqual(Authors)) Authors = [.. authors];
        if (!kinds.SequenceEqual(Kinds)) Kinds = [.. kinds];
        TotalCount = total;
    }

    private void Refresh()
    {
        var shown = new List<Group>();
        foreach (var groups in _cache.Values)
            foreach (var g in groups)
                if (Visible(g)) shown.Add(g);
        shown.Sort(Compare);

        var rows = new List<CommentItem>(shown.Count);
        foreach (var g in shown)
        {
            rows.Add(g.Root);
            rows.AddRange(g.Replies);
        }
        ShownCount = rows.Count;

        // 같은 줄들이면 컬렉션을 건드리지 않는다 (목록의 스크롤·선택이 풀리지 않게)
        bool same = rows.Count == _items.Count;
        for (int i = 0; same && i < rows.Count; i++) same = ReferenceEquals(rows[i], _items[i]);
        if (!same) _items.ReplaceAll(rows);
    }

    private bool Visible(Group g)
    {
        var root = g.Root;
        if (_pageSet is not null && !_pageSet.Contains(root.PageNumber)) return false;
        if (KindFilter.Count > 0 && !KindFilter.Contains(root.KindName)) return false;
        if (StateFilter is { } st && root.State != st) return false;
        bool author = string.IsNullOrEmpty(AuthorFilter);
        string q = SearchText?.Trim() ?? "";
        bool text = q.Length == 0;
        if (author && text) return true;
        if (!author && !MatchesAuthor(root) && !g.Replies.Any(MatchesAuthor)) return false;
        if (!text && !MatchesText(root, q) && !g.Replies.Any(r => MatchesText(r, q))) return false;
        return true;
    }

    private bool MatchesAuthor(CommentItem i) => string.Equals(i.Author, AuthorFilter, StringComparison.CurrentCultureIgnoreCase);

    private static bool MatchesText(CommentItem i, string q)
        => (i.Contents?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
        || (i.Author?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false);

    private int Compare(Group x, Group y)
    {
        int c = SortBy switch
        {
            CommentSort.Date => CompareDates(x.Root.When, y.Root.When, SortDescending),
            CommentSort.Author => CompareAuthors(Blank(x.Root.Author), Blank(y.Root.Author), SortDescending),
            CommentSort.Kind => Flip(AuthorComparer.Compare(x.Root.KindName, y.Root.KindName), SortDescending),
            _ => Flip(x.Root.PageNumber.CompareTo(y.Root.PageNumber), SortDescending),
        };
        if (c != 0) return c;
        // 같은 값이면 읽는 순서: 쪽 → 위에서 아래 → 왼쪽에서 오른쪽 → 만든 순서
        c = x.Root.PageNumber.CompareTo(y.Root.PageNumber);
        if (c != 0) return c;
        c = y.Root.Rect.Top.CompareTo(x.Root.Rect.Top);
        if (c != 0) return c;
        c = x.Root.Rect.Left.CompareTo(y.Root.Rect.Left);
        return c != 0 ? c : x.Seq.CompareTo(y.Seq);
    }

    private static readonly StringComparer AuthorComparer = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static int Flip(int c, bool descending) => descending ? -c : c;

    // 값이 없는 것은 방향과 관계없이 맨 끝
    private static int CompareDates(DateTimeOffset? a, DateTimeOffset? b, bool descending)
    {
        if (a is null) return b is null ? 0 : 1;
        if (b is null) return -1;
        return Flip(a.Value.CompareTo(b.Value), descending);
    }

    private static int CompareAuthors(string? a, string? b, bool descending)
    {
        if (a is null) return b is null ? 0 : 1;
        if (b is null) return -1;
        return Flip(AuthorComparer.Compare(a, b), descending);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_tab.Session is { } s)
        {
            s.Annotations.Changed -= OnStoreChanged;
            s.PagesChanged -= OnPagesChanged;
        }
    }
}
