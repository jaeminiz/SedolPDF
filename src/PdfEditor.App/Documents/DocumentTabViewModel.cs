using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEditor.App.Viewer;
using PdfEditor.Document;

namespace PdfEditor.App.Documents;

public enum ToolKind { Hand, TextSelect }

/// <summary>문서 탭 하나. 세션이 있으면 변경 표시(제목 끝 " •")와 쪽 수를 세션에서 따라간다.</summary>
public sealed partial class DocumentTabViewModel : ObservableObject
{
    private const double PtToMm = 25.4 / 72.0;
    private readonly Action<Action> _post;
    private string _baseTitle;

    [ObservableProperty] private double _scale = 1.0;
    [ObservableProperty] private int _currentPageIndex;
    [ObservableProperty] private int _pageCount;
    [ObservableProperty] private double _pageWidthMm;
    [ObservableProperty] private double _pageHeightMm;
    [ObservableProperty] private ViewMode _viewMode = ViewMode.Continuous;
    [ObservableProperty] private ToolKind _tool = ToolKind.Hand;
    [ObservableProperty] private IReadOnlySet<PageId> _selectedPages = new HashSet<PageId>();

    /// <summary>세션 없이 제목만 가진 탭(테스트·자리 표시용).</summary>
    public DocumentTabViewModel(string title) : this(null, title, null) { }

    /// <param name="post">세션 이벤트(전용 스레드에서 발생)를 UI 스레드로 넘기는 함수. null이면 그 자리에서 실행.</param>
    public DocumentTabViewModel(DocumentSession? session, string title, Action<Action>? post)
    {
        Session = session;
        _baseTitle = title;
        _post = post ?? (a => a());
        if (session is not null)
        {
            ApplyPages();
            session.Commands.Changed += OnSessionChanged;
            session.PagesChanged += OnPagesChanged;
        }
    }

    public DocumentSession? Session { get; }

    /// <summary>탭을 닫을 때(Detach) 발생. 화면이 이 탭의 캐시를 정리하는 데 쓴다.</summary>
    public event Action? Detached;

    /// <summary>문서 화면이 보고하는 보이는 영역 크기(맞춤 배율 계산용).</summary>
    public Size ViewportSize { get; set; }

    /// <summary>페이지 왼쪽 위 기준 커서 위치(mm). 페이지 밖이면 null.</summary>
    [ObservableProperty] private double? _cursorXmm;
    [ObservableProperty] private double? _cursorYmm;

    public void SetCursorFromPagePoint(Point? inPage, double scale)
    {
        if (inPage is not { } p || scale <= 0) { CursorXmm = null; CursorYmm = null; return; }
        CursorXmm = p.X / scale * PtToMm;
        CursorYmm = p.Y / scale * PtToMm;
    }

    public void ZoomIn() => Scale = ZoomMath.Step(Scale, 1);
    public void ZoomOut() => Scale = ZoomMath.Step(Scale, -1);
    public void ActualSize() => Scale = 1.0;

    private bool TryCurrentPage(out PageInfo page)
    {
        page = default!;
        var pages = Session?.Pages;
        if (pages is null || pages.Value.Length == 0) return false;
        page = pages.Value[Math.Clamp(CurrentPageIndex, 0, pages.Value.Length - 1)];
        return true;
    }

    public void FitWidth()
    {
        if (TryCurrentPage(out var p) && ViewportSize.Width > 0)
            Scale = ZoomMath.FitWidth(new Size(p.WidthPt, p.HeightPt), ViewportSize.Width);
    }

    public void FitPage()
    {
        if (TryCurrentPage(out var p) && ViewportSize.Width > 0 && ViewportSize.Height > 0)
            Scale = ZoomMath.FitPage(new Size(p.WidthPt, p.HeightPt), ViewportSize);
    }

    private void GoTo(int i) => CurrentPageIndex = Math.Clamp(i, 0, Math.Max(0, PageCount - 1));
    public void FirstPage() => GoTo(0);
    public void LastPage() => GoTo(PageCount - 1);
    public void NextPage() => GoTo(CurrentPageIndex + 1);
    public void PrevPage() => GoTo(CurrentPageIndex - 1);

    public string Title => Session is { IsDirty: true } ? _baseTitle + " •" : _baseTitle;

    /// <summary>다른 이름으로 저장 후 등 세션의 이름·변경 여부가 바뀌었을 때 부른다.</summary>
    public void RefreshTitle()
    {
        if (Session is not null) _baseTitle = Session.DisplayName;
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>이벤트 구독을 풀고, 세션 해제는 호출자가 한다.</summary>
    public void Detach()
    {
        Detached?.Invoke();
        if (Session is null) return;
        Session.Commands.Changed -= OnSessionChanged;
        Session.PagesChanged -= OnPagesChanged;
    }

    private void OnSessionChanged() => _post(RefreshTitle);
    private void OnPagesChanged(PagesChangedEventArgs e) => _post(ApplyPages);

    private void ApplyPages()
    {
        var pages = Session!.Pages;
        PageCount = pages.Length;
        if (pages.Length == 0) return;
        int i = Math.Clamp(CurrentPageIndex, 0, pages.Length - 1);
        PageWidthMm = pages[i].WidthPt * PtToMm;
        PageHeightMm = pages[i].HeightPt * PtToMm;
        if (i != CurrentPageIndex) CurrentPageIndex = i;
    }

    // 현재 페이지가 바뀌면 상태 표시줄의 페이지 크기도 그 페이지 것으로 바꾼다.
    partial void OnCurrentPageIndexChanged(int value)
    {
        var pages = Session?.Pages;
        if (pages is null || pages.Value.Length == 0) return;
        var p = pages.Value[Math.Clamp(value, 0, pages.Value.Length - 1)];
        PageWidthMm = p.WidthPt * PtToMm;
        PageHeightMm = p.HeightPt * PtToMm;
    }
}
