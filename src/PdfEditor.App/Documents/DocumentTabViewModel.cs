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
}
