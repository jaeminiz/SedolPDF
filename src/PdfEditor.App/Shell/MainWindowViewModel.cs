using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Printing;
using PdfEditor.App.Resources;
using PdfEditor.App.Shell.Ribbon;
using PdfEditor.App.Viewer;
using PdfEditor.App.Viewer.Annotations;

namespace PdfEditor.App.Shell;

/// <summary>앱 셸의 상태. 문서 관련 명령은 자리 표시용(CanExecute false)이고 Task 11~17에서 연결한다.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private const double MinZoomPercent = 10, MaxZoomPercent = 1600;
    private static readonly double ZoomLogSpan = Math.Log(MaxZoomPercent / MinZoomPercent);
    private readonly Dictionary<string, IReadOnlyList<RibbonGroupVm>> _groupCache = new();
    private readonly List<IRelayCommand> _docCommands = [];
    private bool _syncingDocZoom;
    private readonly List<RibbonButtonVm> _toolButtons = [];
    private RibbonButtonVm? _oneLineButton;
    private bool _oneLineOnly;

    /// <summary>"한 줄만"(편집 탭, M5 C1): 켜면 텍스트 편집 도구가 문단으로 묶지 않고 눌러 고른 한 줄만 고친다. 모든 문서에 적용된다.</summary>
    public bool OneLineOnly
    {
        get => _oneLineOnly;
        set
        {
            if (_oneLineOnly == value) return;
            _oneLineOnly = value;
            foreach (var d in Documents) d.OneLineOnly = value;
            if (_oneLineButton is not null) _oneLineButton.IsChecked = value;
            OnPropertyChanged();
        }
    }

    /// <summary>활성 문서(세션 있는)에만 실행되는 명령. 활성 문서가 바뀌면 CanExecute를 다시 알린다.</summary>
    private RelayCommand NavCmd(Action<DocumentTabViewModel> a) => DocCmd(a);

    private RelayCommand DocCmd(Action<DocumentTabViewModel> action)
    {
        var c = new RelayCommand(() => { if (ActiveDocument is { Session: not null } d) action(d); },
            () => ActiveDocument?.Session is not null);
        _docCommands.Add(c);
        return c;
    }

    /// <summary>활성 문서의 쪽 편집 명령을 그대로 부르는 명령. 실행 가능 여부도 그 명령을 따른다.</summary>
    private RelayCommand PageCmd(Func<PageCommandsViewModel, IAsyncRelayCommand> pick)
    {
        var c = new RelayCommand(
            () => { if (ActiveDocument?.PageCommands is { } p && pick(p).CanExecute(null)) _ = pick(p).ExecuteAsync(null); },
            () => ActiveDocument?.PageCommands is { } p && pick(p).CanExecute(null));
        _docCommands.Add(c);
        return c;
    }

    /// <summary>
    /// 주석 도구를 고르는 명령. 주석 권한이 없는 문서에서는 비활성이다. 저장 때 PDFsharp 패치가 필요한 도구는
    /// 패치를 못 하는(암호) 문서에서도 비활성이다 — 켜 두면 만든 주석이 저장 때 조용히 사라진다 (A2).
    /// </summary>
    private static bool IsContentTool(ToolKind t) => t is ToolKind.EditObject or ToolKind.EditText or ToolKind.AddText or ToolKind.AddShape or ToolKind.AddEllipse or ToolKind.AddLine;

    private RelayCommand ToolCmd(ToolKind tool)
    {
        bool Allowed(DocumentTabViewModel d) => IsContentTool(tool)
            ? d.CanEditContent && (tool != ToolKind.AddText || d.CanUsePatchFeatures)   // 새 텍스트는 앱 글꼴 줄이라 패치 단계가 필요하다(암호 문서는 안 됨)
            : d.CanAnnotate && (!AnnotationToolController.IsPatchTool(tool) || d.CanUsePatchFeatures);
        var c = new RelayCommand(
            () => { if (ActiveDocument is { Session: not null } d && Allowed(d)) d.Tool = tool; },
            () => ActiveDocument is { Session: not null } d && Allowed(d));
        _docCommands.Add(c);
        return c;
    }

    public IRelayCommand FirstPageCommand { get; private set; } = null!;
    public IRelayCommand PrevPageCommand { get; private set; } = null!;
    public IRelayCommand NextPageCommand { get; private set; } = null!;
    public IRelayCommand LastPageCommand { get; private set; } = null!;
    private bool _syncingZoom;

    public ObservableCollection<DocumentTabViewModel> Documents { get; } = [];
    public CommandRegistry Commands { get; } = new();
    public IReadOnlyDictionary<string, IReadOnlyList<RibbonGroupModel>> Ribbon { get; }
    public IReadOnlyList<RibbonTabVm> RibbonTabs { get; }
    public IReadOnlyList<PanelButtonVm> PanelButtons { get; }
    public IReadOnlyList<string> FileMenuCommandIds { get; } =
        ["file.new", "file.open", "file.save", "file.saveAs", "file.print", "file.prefs", "file.about", "file.exit"];
    public IReadOnlyList<AppCommand> QuickAccessCommands => QuickAccessCommandIds.Select(i => Commands.Find(i)!).ToList();
    public ICommand OpenCommand => Commands.Find("file.open")!.Command;
    public IReadOnlyList<string> QuickAccessCommandIds { get; } =
        ["file.open", "file.save", "file.print", "edit.undo", "edit.redo"];

    public IRelayCommand<PanelKind> SetPanelCommand { get; }
    public IRelayCommand<DocumentTabViewModel> CloseDocumentCommand { get; }
    private FileWorkflow? _workflow;
    private IUserPrompts? _prompts;
    private Action<Action> _post = a => a();
    private IPrintService? _printService;
    private AsyncRelayCommand _printCommand = null!;
    private AsyncRelayCommand _newCommand = null!, _openCommand = null!, _saveCommand = null!, _saveAsCommand = null!, _closeActiveCommand = null!;
    public IRelayCommand<string> SelectRibbonTabCommand { get; }

    /// <summary>끝내기 명령이 실행되면 창이 닫는다.</summary>
    public event EventHandler? ExitRequested;
    public event EventHandler? PreferencesRequested;

    /// <summary>이미지 추가(편집 탭)에서 PNG·JPEG 파일을 골라 바이트로 돌려주는 대화상자(취소하면 null). 메인 창이 연결한다.</summary>
    public Func<Task<byte[]?>>? PickImageBytes { get; set; }
    private AsyncRelayCommand _addImageCommand = null!;

    private async Task AddImageAsync()
    {
        if (ActiveDocument is not { Session: not null, CanEditContent: true } d || PickImageBytes is null) return;
        byte[]? data;
        try { data = await PickImageBytes(); }
        catch (Exception) { d.RaiseNotice(Strings.Msg_ImageInsertFailed); return; }
        if (data is not null) await d.AddImageAsync(data);
    }
    public event EventHandler? AboutRequested;
    public event EventHandler? HelpRequested;

    /// <summary>찾기 명령이 검색 패널을 열었다. 창이 검색어 입력칸에 초점을 준다.</summary>
    public event EventHandler? FocusSearchRequested;

    /// <summary>찾아 바꾸기 명령이 바꾸기 패널을 열었다. 창이 찾을 글 입력칸에 초점을 준다.</summary>
    public event EventHandler? FocusReplaceRequested;

    /// <summary>바꾸기 패널을 연다. query가 있으면 찾을 글에 채운다(이미 입력한 글은 query가 비었을 때만 남긴다).</summary>
    public void OpenReplacePanel(DocumentTabViewModel? tab = null, string? query = null)
    {
        tab ??= ActiveDocument;
        if (tab is null || !ReferenceEquals(tab, ActiveDocument)) return;
        if (!string.IsNullOrEmpty(query) && tab.Replace is { } r) r.FindText = query;
        ActiveLeftPanel = PanelKind.Replace;
        IsLeftPanelVisible = true;
        FocusReplaceRequested?.Invoke(this, EventArgs.Empty);
    }

    [ObservableProperty] private DocumentTabViewModel? _activeDocument;
    [ObservableProperty] private string _selectedRibbonTab = "Home";
    [ObservableProperty] private PanelKind _activeLeftPanel = PanelKind.Thumbnails;
    [ObservableProperty] private bool _isLeftPanelVisible = true;
    [ObservableProperty] private bool _isPropertiesPanelVisible = true;
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private string _quickCommandText = "";
    [ObservableProperty] private IReadOnlyList<AppCommand> _quickCommandResults = [];
    [ObservableProperty] private double _zoomPercent = 100;
    [ObservableProperty] private double _zoomSliderValue = Math.Log(10) / ZoomLogSpan;

    public MainWindowViewModel()
    {
        SetPanelCommand = new RelayCommand<PanelKind>(k => { ActiveLeftPanel = k; IsLeftPanelVisible = true; });
        SelectRibbonTabCommand = new RelayCommand<string>(t => { if (t is not null && (t != FormatTabId || IsFormatTabVisible)) SelectedRibbonTab = t; });
        CloseDocumentCommand = new RelayCommand<DocumentTabViewModel>(d => { if (d is not null) _ = CloseDocumentAsync(d); });

        FirstPageCommand = NavCmd(d => d.FirstPage());
        PrevPageCommand = NavCmd(d => d.PrevPage());
        NextPageCommand = NavCmd(d => d.NextPage());
        LastPageCommand = NavCmd(d => d.LastPage());
        RegisterCommands();
        Ribbon = BuildRibbon();
        RibbonTabs =
        [
            new("Home", Strings.Tab_Home), new("Annotate", Strings.Tab_Annotate), new("Edit", Strings.Tab_Edit),
            new("Page", Strings.Tab_Page), new("View", Strings.Tab_View), new("Help", Strings.Tab_Help),
        ];
        RibbonTabs[0].IsSelected = true;
        PanelButtons =
        [
            new(PanelKind.Thumbnails, Strings.Panel_Thumbnails, "thumbs"), new(PanelKind.Bookmarks, Strings.Panel_Bookmarks, "bookmark"),
            new(PanelKind.Comments, Strings.Panel_Comments, "comments"), new(PanelKind.Attachments, Strings.Panel_Attachments, "clip"),
            new(PanelKind.Layers, Strings.Panel_Layers, "layers"), new(PanelKind.Search, Strings.Panel_Search, "search"),
            new(PanelKind.Replace, Strings.Panel_Replace, "findreplace"),
        ];
        PanelButtons[0].IsSelected = true;
    }

    // ---- 화면에 바인딩되는 파생 값 ----
    public IReadOnlyList<RibbonGroupVm> CurrentRibbonGroups => GroupsFor(SelectedRibbonTab);
    public string ActivePanelLabel => PanelButtons.First(p => p.Kind == ActiveLeftPanel).Label;
    public string FileName => ActiveDocument?.Title ?? Strings.Doc_NoneOpen;
    public bool HasNoDocument => ActiveDocument is null;
    public ThumbnailsViewModel? ActiveThumbnails => ActiveDocument?.Thumbnails;
    public bool ShowThumbnails => ActiveLeftPanel == PanelKind.Thumbnails;
    public bool ShowBookmarks => ActiveLeftPanel == PanelKind.Bookmarks;
    public bool ShowAttachments => ActiveLeftPanel == PanelKind.Attachments;
    public bool ShowLayers => ActiveLeftPanel == PanelKind.Layers;
    public bool ShowComments => ActiveLeftPanel == PanelKind.Comments;
    public bool ShowSearch => ActiveLeftPanel == PanelKind.Search;
    public bool ShowReplace => ActiveLeftPanel == PanelKind.Replace;
    public FindReplaceViewModel? ActiveReplace => ActiveDocument?.Replace;
    public SearchViewModel? ActiveSearch => ActiveDocument?.Search;
    public BookmarksViewModel? ActiveBookmarks => ActiveDocument?.Bookmarks;
    public AttachmentsViewModel? ActiveAttachments => ActiveDocument?.Attachments;
    public CommentsViewModel? ActiveComments => ActiveDocument?.Comments;

    /// <summary>왼쪽 패널 폭: 코멘트 목록은 글이 길어 더 넓게.</summary>
    public double LeftPanelWidth => ActiveLeftPanel is PanelKind.Comments ? 320 : ActiveLeftPanel is PanelKind.Replace ? 340 : 200;
    public LayersViewModel? ActiveLayers => ActiveDocument?.Layers;
    public PropertiesViewModel? ActiveProperties => ActiveDocument?.Properties;
    public PdfEditor.App.Panels.ObjectPropertiesViewModel? ActiveObjectProps => ActiveDocument?.ObjectProps;

    // ---- "형식" 탭 (M3b B5): 만들기 도구가 켜졌거나 주석이 선택되면 메뉴 탭 오른쪽 끝에 나타난다 ----
    public const string FormatTabId = "Format";
    public AnnotationStylesViewModel? ActiveStyles => ActiveDocument?.Styles;
    /// <summary>속성 패널과 같은 모델 (둘이 같은 인스턴스라 값이 항상 같다).</summary>
    public AnnotationPropertiesViewModel? ActiveAnnotationProperties => ActiveDocument?.Properties?.Annotation;
    public bool IsFormatTabVisible => ActiveDocument?.Styles is { HasTarget: true };
    public bool IsFormatTabSelected => SelectedRibbonTab == FormatTabId && IsFormatTabVisible;
    public string FormatTabSubtitle => ActiveDocument?.Styles?.TargetText ?? "";

    private void NotifyFormatTab()
    {
        // 보이지 않게 된 "형식" 탭이 선택돼 있으면 주석 탭으로 돌아간다.
        if (SelectedRibbonTab == FormatTabId && !IsFormatTabVisible) SelectedRibbonTab = "Annotate";
        OnPropertyChanged(nameof(IsFormatTabVisible));
        OnPropertyChanged(nameof(IsFormatTabSelected));
        OnPropertyChanged(nameof(FormatTabSubtitle));
    }
    public bool HasQuickResults => QuickCommandResults.Count > 0;
    public string ZoomText => $"{ZoomPercent:0}%";
    public int PageCount => ActiveDocument?.PageCount ?? 0;
    public string PageCountText => string.Format(Strings.Status_PageCount, PageCount);
    public string StatusSizeText
    {
        get
        {
            var d = ActiveDocument;
            string size = string.Format(Strings.Status_PageSize, d?.PageWidthMm ?? 0, d?.PageHeightMm ?? 0);
            return d is { CursorXmm: { } x, CursorYmm: { } y } ? size + "  " + string.Format(Strings.Status_Cursor, x, y) : size;
        }
    }

    /// <summary>1부터 시작하는 현재 페이지 번호(문서 없으면 0). 쓰면 문서의 현재 페이지가 바뀐다.</summary>
    public int CurrentPageNumber
    {
        get => ActiveDocument is null ? 0 : ActiveDocument.CurrentPageIndex + 1;
        set
        {
            if (ActiveDocument is null) return;
            ActiveDocument.CurrentPageIndex = Math.Clamp(value - 1, 0, Math.Max(0, ActiveDocument.PageCount - 1));
            OnPropertyChanged();
        }
    }

    partial void OnActiveDocumentChanged(DocumentTabViewModel? oldValue, DocumentTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnDocumentPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnDocumentPropertyChanged;
        if (newValue is not null) newValue.OneLineOnly = OneLineOnly;
        RefreshDocumentStatus();
        RefreshToolButtons();
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(HasNoDocument));
        OnPropertyChanged(nameof(PrintDisabledTip));
        NotifyFileCommands();
        foreach (var c in _docCommands) c.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ActiveThumbnails));
        OnPropertyChanged(nameof(ActiveBookmarks));
        OnPropertyChanged(nameof(ActiveAttachments));
        OnPropertyChanged(nameof(ActiveComments));
        OnPropertyChanged(nameof(ActiveLayers));
        OnPropertyChanged(nameof(ActiveProperties));
        OnPropertyChanged(nameof(ActiveObjectProps));
        OnPropertyChanged(nameof(ActiveStyles));
        OnPropertyChanged(nameof(ActiveAnnotationProperties));
        OnPropertyChanged(nameof(ActiveSearch));
        OnPropertyChanged(nameof(ActiveReplace));
        NotifyFormatTab();
        if (newValue is not null) SetZoomFromDocument(newValue.Scale);
        LoadVisiblePanels();
    }

    /// <summary>상태줄 안내: 글 선택 도구일 때만 사각형 선택(Alt+끌기)을 알린다.</summary>
    public string TextSelectHint => ActiveDocument?.Tool == ToolKind.TextSelect ? Strings.Status_RectSelectHint : "";

    /// <summary>활성 문서의 도구와 같은 도구 단추를 켜진 상태로 보인다.</summary>
    private void RefreshToolButtons()
    {
        var tool = ActiveDocument?.Tool;
        OnPropertyChanged(nameof(TextSelectHint));
        foreach (var b in _toolButtons)
        {
            b.IsChecked = tool is not null && b.Tool == tool;
            ApplyPatchTip(b);
        }
    }

    // 암호 문서에서 꺼진 패치 도구에는 이유를 알리는 툴팁을 보인다.
    private void ApplyPatchTip(RibbonButtonVm b)
    {
        bool noPatch = ActiveDocument is { Session: not null, CanAnnotate: true, CanUsePatchFeatures: false };
        b.Tip = noPatch && b.Tool is { } t && AnnotationToolController.IsPatchTool(t)
            ? Strings.Msg_PatchFeaturesUnavailableEncrypted
            : b.Tool == ToolKind.AddText && ActiveDocument is { Session: not null, CanEditContent: true, CanUsePatchFeatures: false }
                ? Strings.Msg_AddTextDisabledEncrypted
                : b.Tool is { } ct && IsContentTool(ct) && ActiveDocument is { Session: not null, CanEditContent: false }
                    ? Strings.Msg_EditRestricted : b.BaseTip;
    }

    private void NotifyFileCommands()
    {
        _newCommand.NotifyCanExecuteChanged();
        _openCommand.NotifyCanExecuteChanged();
        _saveCommand.NotifyCanExecuteChanged();
        _saveAsCommand.NotifyCanExecuteChanged();
        _closeActiveCommand.NotifyCanExecuteChanged();
        _printCommand.NotifyCanExecuteChanged();
    }

    // ---- 파일 작업 (Task 11) ----

    /// <summary>파일 작업을 연결한다. post는 전용 스레드에서 오는 세션 이벤트를 UI 스레드로 넘긴다.</summary>
    public void AttachWorkflow(FileWorkflow workflow, IUserPrompts prompts, Action<Action>? post = null)
    {
        _workflow = workflow;
        _prompts = prompts;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        NotifyFileCommands();
    }

    public IReadOnlyList<string> RecentFiles => _workflow?.RecentFiles ?? [];

    private async Task NewDocumentAsync()
    {
        if (_workflow is null) return;
        try { Activate(await _workflow.NewAsync()); }
        catch (Exception ex) { await ShowErrorAsync(ex); }
    }

    private async Task OpenFromDialogAsync()
    {
        if (_workflow is null || _prompts is null) return;
        await OpenManyAsync(await _prompts.PickOpenFilesAsync());
    }

    public async Task OpenManyAsync(IEnumerable<string> paths)
    {
        foreach (var p in paths) await OpenAsync(p);
    }

    /// <summary>파일을 열어 탭으로 보여 준다. 이미 열린 파일이면 그 탭으로 전환한다.</summary>
    public async Task OpenAsync(string path)
    {
        if (_workflow is null) return;
        var session = await _workflow.OpenAsync(path);
        if (session is not null) Activate(session);
    }

    /// <summary>다른 곳에서 열린 세션(복구 등)을 탭으로 보여 준다.</summary>
    public void ShowSession(PdfEditor.Document.DocumentSession session) => Activate(session);

    private void Activate(PdfEditor.Document.DocumentSession session)
    {
        var tab = Documents.FirstOrDefault(d => ReferenceEquals(d.Session, session));
        if (tab is null)
        {
            tab = new DocumentTabViewModel(session, session.DisplayName, _post);
            AttachPageEditing(tab);
            Documents.Add(tab);
        }
        ActiveDocument = tab;
    }

    /// <summary>탭에 쪽 편집 명령과 썸네일 모델을 붙인다. 탭을 닫을 때(Detached) 함께 해제한다.</summary>
    private void AttachPageEditing(DocumentTabViewModel tab)
    {
        var cmds = new PageCommandsViewModel(tab, _workflow!, _prompts!, _post);
        cmds.SessionCreated += s => Activate(s);
        cmds.StateChanged += () => { if (ReferenceEquals(ActiveDocument, tab)) NotifyDocCommands(); };
        var thumbs = new ThumbnailsViewModel(tab, (data, w, h) => new BitmapHandle(data, w, h), _post);
        tab.PageCommands = cmds;
        tab.Thumbnails = thumbs;
        tab.Bookmarks = new BookmarksViewModel(tab);
        tab.Attachments = new AttachmentsViewModel(tab, _prompts!);
        tab.Layers = new LayersViewModel(tab);
        tab.Properties = new PropertiesViewModel(tab);
        var styles = tab.Styles = new AnnotationStylesViewModel(tab, tab.Properties.Annotation);
        styles.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnnotationStylesViewModel.TargetText) && ReferenceEquals(ActiveDocument, tab)) NotifyFormatTab();
        };
        var search = tab.Search = new SearchViewModel(tab);
        tab.NoticeRequested += msg => { _ = _prompts!.ShowInfoAsync(msg); };
        // 우클릭 메뉴의 "선택한 글 찾기": 검색 패널을 열고 그 글로 바로 찾는다.
        tab.FindTextRequested += text =>
        {
            if (!ReferenceEquals(ActiveDocument, tab)) return;
            search.Query = text;
            ActiveLeftPanel = PanelKind.Search;
            IsLeftPanelVisible = true;
            FocusSearchRequested?.Invoke(this, EventArgs.Empty);
            if (search.SearchCommand.CanExecute(null)) _ = search.SearchCommand.ExecuteAsync(null);
        };
        var replace = tab.Replace = new FindReplaceViewModel(tab);
        replace.CloseRequested += () => { if (ReferenceEquals(ActiveDocument, tab)) IsLeftPanelVisible = false; };
        // 검색 패널의 "찾아 바꾸기…" 단추: 찾던 글을 가지고 바꾸기 패널로 간다.
        search.OpenReplaceRequested += () => OpenReplacePanel(tab, search.Query);
        tab.Detached += () => { search.Clear(); replace.Dispose(); thumbs.Dispose(); cmds.Dispose(); tab.Comments?.Dispose(); tab.Comments = null; };
    }

    private void NotifyDocCommands()
    {
        foreach (var c in _docCommands) c.NotifyCanExecuteChanged();
    }

    // ---- 인쇄 (Task 16) ----

    /// <summary>인쇄 서비스를 연결한다. 연결 전에는 인쇄 명령이 비활성이다.</summary>
    public void AttachPrinting(IPrintService service)
    {
        _printService = service;
        _printCommand.NotifyCanExecuteChanged();
    }

    private bool CanPrintActive() => _printService is not null && ActiveDocument?.Session is { Permissions.CanPrint: true };

    /// <summary>인쇄가 제한된 문서면 비활성 사유(툴팁), 아니면 null.</summary>
    public string? PrintDisabledTip => ActiveDocument?.Session is { Permissions.CanPrint: false } ? Strings.Msg_PrintRestricted : null;

    private async Task PrintActiveAsync()
    {
        if (_printService is null || ActiveDocument is not { Session: { } s } d) return;
        try { await _printService.PrintAsync(s, d.CurrentPageIndex); }
        catch (Exception ex) { await ShowErrorAsync(ex); }
    }

    private async Task SaveActiveAsync()
    {
        if (_workflow is null || ActiveDocument?.Session is not { } s) return;
        await _workflow.SaveAsync(s);
        ActiveDocument?.RefreshTitle();
    }

    private async Task SaveActiveAsAsync()
    {
        if (_workflow is null || ActiveDocument?.Session is not { } s) return;
        await _workflow.SaveAsAsync(s);
        ActiveDocument?.RefreshTitle();
    }

    /// <summary>
    /// 탭을 끌어 놓은 자리로 옮긴다. slot = 놓은 자리(그 번호의 탭 앞, 탭 수면 맨 끝). 문서·편집 내용은 그대로이고 순서만 바뀌며
    /// 고른 탭도 유지한다. 순서가 달라지면 true.
    /// </summary>
    public bool MoveDocument(DocumentTabViewModel tab, int slot)
    {
        int from = Documents.IndexOf(tab);
        if (from < 0) return false;
        int to = TabReorder.FinalIndex(from, slot, Documents.Count);
        if (to == from) return false;
        var active = ActiveDocument;
        Documents.Move(from, to);
        if (!ReferenceEquals(ActiveDocument, active)) ActiveDocument = active;
        return true;
    }

    /// <summary>탭을 닫는다. 변경이 있으면 먼저 묻고, 취소하면 그대로 둔다.</summary>
    public async Task<bool> CloseDocumentAsync(DocumentTabViewModel d)
    {
        var session = d.Session;
        if (session is not null && _workflow is not null && !await _workflow.ConfirmCloseAsync(session)) return false;
        int i = Documents.IndexOf(d);
        if (i < 0) return true;
        Documents.RemoveAt(i);
        if (ActiveDocument == d)
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Min(i, Documents.Count - 1)];
        d.Detach();
        if (session is not null)
        {
            _workflow?.Release(session);
            await session.DisposeAsync();
        }
        return true;
    }

    /// <summary>앱을 닫아도 되는지: 열린 탭마다 확인하고 하나라도 취소면 false. 닫기로 한 탭은 정리한다.</summary>
    public async Task<bool> ConfirmExitAsync()
    {
        foreach (var d in Documents.ToList())
            if (!await CloseDocumentAsync(d)) return false;
        return true;
    }

    /// <summary>XFDF 내보내기(M3b Task 5): 저장 위치를 묻고 문서의 모든 주석을 쓴다. 실패는 알림으로.</summary>
    private async Task ExportXfdfAsync(DocumentTabViewModel d)
    {
        if (_prompts is null || d.Session is not { } s) return;
        try
        {
            string suggested = Path.ChangeExtension(s.DisplayName, ".xfdf");
            if (await _prompts.PickSaveXfdfAsync(suggested) is not { } path) return;
            int n = await XfdfExporter.ExportAsync(s, DocumentTabViewModel.Appearance?.Assets ?? PdfEditor.Document.Annotations.AnnotationAssets.Empty, path);
            await _prompts.ShowInfoAsync(string.Format(Strings.Msg_XfdfExported, n));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ShowErrorAsync(ex);
        }
    }

    /// <summary>
    /// 주석 요약 PDF(M3b Task 7): 주석이 있는지 보고, 저장 위치(기본 "&lt;PDF 이름&gt;-summary.pdf")를 묻고, 진행 창(취소 가능)에서 만든다.
    /// 쪽 그림 그리기·PDF 쓰기는 UI 스레드 밖에서 하므로 큰 문서에서도 화면이 멈추지 않는다. 실패는 알림으로.
    /// </summary>
    public async Task SummaryAsync(DocumentTabViewModel d)
    {
        if (_prompts is null || d.Session is not { } s) return;
        try
        {
            await s.AnnotationsReady;
            var plans = AnnotationSummaryRunner.Collect(d);
            if (plans.Count == 0) { await _prompts.ShowInfoAsync(Strings.Msg_SummaryNoAnnotations); return; }
            string suggested = Path.GetFileNameWithoutExtension(s.DisplayName) + "-summary.pdf";
            if (await _prompts.PickSaveSummaryPdfAsync(suggested) is not { } path) return;
            var svc = DocumentTabViewModel.Appearance ?? throw new FileNotFoundException(Strings.Err_SummaryNoFont);
            SummaryOutcome? outcome = null;
            bool finished = await _prompts.RunWithProgressAsync(Strings.Dlg_SummaryProgressTitle, Strings.Dlg_SummaryProgress, plans.Count,
                async (progress, ct) => outcome = await AnnotationSummaryRunner.RunAsync(d, svc, plans, path, progress, ct));
            if (!finished || outcome is null) return;
            string msg = string.Format(Strings.Msg_SummaryDone, outcome.AnnotationCount, outcome.SummaryPages);
            if (outcome.MissingGlyphs > 0) msg += Environment.NewLine + string.Format(Strings.Msg_SummaryMissingGlyphs, outcome.MissingGlyphs);
            await _prompts.ShowInfoAsync(msg);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ShowErrorAsync(ex);
        }
    }

    /// <summary>XFDF 가져오기(M3b Task 6): 파일을 고르고 주석을 가져와 결과(건너뜀 포함)를 알린다. 실패는 알림으로, 앱은 죽지 않는다.</summary>
    private async Task ImportXfdfAsync(DocumentTabViewModel d)
    {
        if (_prompts is null || d.Session is null) return;
        try
        {
            if (await _prompts.PickOpenXfdfAsync() is not { } path) return;
            var outcome = await XfdfImporter.ImportAsync(d, DocumentTabViewModel.Appearance?.Assets ?? PdfEditor.Document.Annotations.AnnotationAssets.Empty, path);
            await _prompts.ShowXfdfImportResultAsync(string.Format(Strings.Msg_XfdfImported, outcome.Imported, outcome.SkippedCount), outcome.Details);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ShowErrorAsync(ex);
        }
    }

    private async Task ShowErrorAsync(Exception ex)
    {
        if (_prompts is not null) await _prompts.ShowErrorAsync(ErrorMessages.For(ex));
    }

    private void OnDocumentPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        RefreshDocumentStatus();
        if (e.PropertyName == nameof(DocumentTabViewModel.Tool)) RefreshToolButtons();
        if (e.PropertyName == nameof(DocumentTabViewModel.Scale) && s is DocumentTabViewModel d) SetZoomFromDocument(d.Scale);
        if (e.PropertyName == nameof(DocumentTabViewModel.Title)) OnPropertyChanged(nameof(FileName));
    }

    private void RefreshDocumentStatus()
    {
        OnPropertyChanged(nameof(CurrentPageNumber));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageCountText));
        OnPropertyChanged(nameof(StatusSizeText));
    }


    private void SetZoomFromDocument(double scale)
    {
        _syncingDocZoom = true;
        try { ZoomPercent = Math.Clamp(scale * 100, MinZoomPercent, MaxZoomPercent); }
        finally { _syncingDocZoom = false; }
    }

    partial void OnSelectedRibbonTabChanged(string value)
    {
        foreach (var t in RibbonTabs) t.IsSelected = t.Id == value;
        OnPropertyChanged(nameof(CurrentRibbonGroups));
        OnPropertyChanged(nameof(IsFormatTabSelected));
    }

    partial void OnActiveLeftPanelChanged(PanelKind value)
    {
        foreach (var p in PanelButtons) p.IsSelected = p.Kind == value;
        OnPropertyChanged(nameof(ActivePanelLabel));
        OnPropertyChanged(nameof(ShowThumbnails));
        OnPropertyChanged(nameof(ShowBookmarks));
        OnPropertyChanged(nameof(ShowAttachments));
        OnPropertyChanged(nameof(ShowLayers));
        OnPropertyChanged(nameof(ShowComments));
        OnPropertyChanged(nameof(LeftPanelWidth));
        OnPropertyChanged(nameof(ShowSearch));
        OnPropertyChanged(nameof(ShowReplace));
        LoadVisiblePanels();
    }

    partial void OnIsLeftPanelVisibleChanged(bool value) => LoadVisiblePanels();
    partial void OnIsPropertiesPanelVisibleChanged(bool value) => LoadVisiblePanels();

    /// <summary>보이는 읽기 전용 패널의 내용을 처음 한 번 읽는다(각 모델이 중복 호출을 무시한다).</summary>
    private async void LoadVisiblePanels()
    {
        if (ActiveDocument is not { } d) return;
        try
        {
            if (IsLeftPanelVisible)
            {
                switch (ActiveLeftPanel)
                {
                    case PanelKind.Bookmarks when d.Bookmarks is { } b: await b.LoadAsync(); break;
                    case PanelKind.Attachments when d.Attachments is { } a: await a.LoadAsync(); break;
                    case PanelKind.Layers when d.Layers is { } l: await l.LoadAsync(); break;
                    case PanelKind.Comments when d.Comments is null:
                        d.Comments = new CommentsViewModel(d);   // 처음 열 때 만든다. 탭을 닫을 때(Detached) 해제한다
                        OnPropertyChanged(nameof(ActiveComments));
                        break;
                }
            }
            if (IsPropertiesPanelVisible && d.Properties is { } p) await p.LoadAsync();
        }
        catch (Exception)
        {
            // 각 모델이 읽기 실패를 스스로 처리한다. 여기서는 async void가 앱을 끝내지 않도록만 막는다.
        }
    }

    partial void OnQuickCommandTextChanged(string value) => QuickCommandResults = Commands.Search(value);
    partial void OnQuickCommandResultsChanged(IReadOnlyList<AppCommand> value) => OnPropertyChanged(nameof(HasQuickResults));

    // 슬라이더(0..1, 로그 눈금)와 배율(%)을 서로 맞춘다.
    partial void OnZoomSliderValueChanged(double value)
    {
        if (_syncingZoom) return;
        _syncingZoom = true;
        ZoomPercent = MinZoomPercent * Math.Exp(Math.Clamp(value, 0, 1) * ZoomLogSpan);
        _syncingZoom = false;
    }

    partial void OnZoomPercentChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomText));
        if (!_syncingDocZoom && ActiveDocument is { } doc && Math.Abs(doc.Scale * 100 - value) > 1e-6) doc.Scale = value / 100.0;
        if (_syncingZoom) return;
        _syncingZoom = true;
        ZoomSliderValue = Math.Log(Math.Clamp(value, MinZoomPercent, MaxZoomPercent) / MinZoomPercent) / ZoomLogSpan;
        _syncingZoom = false;
    }

    private IReadOnlyList<RibbonGroupVm> GroupsFor(string tab)
    {
        if (tab == FormatTabId) return [];   // "형식" 탭은 단추 묶음이 아니라 FormatTab 컨트롤이 그린다
        if (_groupCache.TryGetValue(tab, out var cached)) return cached;
        var groups = Ribbon[tab].Select(g => new RibbonGroupVm(g.Name,
            g.Items.Select(i => MakeButton(i, !g.Compact || i.Large, g.IconOnly && !i.Large)).ToList(),
            g.Compact)).ToList();
        return _groupCache[tab] = groups;
    }

    private RibbonButtonVm MakeButton(RibbonItemModel i, bool large, bool iconOnly = false)
    {
        var more = i.MoreItems?.Select(m => MakeButton(m, large: false)).ToList();
        var b = new RibbonButtonVm(Commands.Find(i.CommandId)!, i.Label, large, i.DisabledTip ?? (iconOnly ? i.Label : null), i.Tool, more) { IconOnly = iconOnly };
        b.IsChecked = i.Tool is { } t && ActiveDocument?.Tool == t;
        if (i.CommandId == "edit.oneLineOnly") { b.IsChecked = OneLineOnly; _oneLineButton = b; }
        if (i.Tool is not null) { _toolButtons.Add(b); ApplyPatchTip(b); }
        return b;
    }

    // ---- 명령 등록 ----
    private static RelayCommand Placeholder() => new(() => { }, () => false);
    private static KeyGesture G(Key k, KeyModifiers m = KeyModifiers.Control) => new(k, m);

    private void Reg(string id, string label, string? icon, KeyGesture? gesture = null, ICommand? command = null) =>
        Commands.Register(new AppCommand(id, label, command ?? Placeholder(), gesture, icon,
            command is null && id.StartsWith("annot.", StringComparison.Ordinal) ? Strings.Msg_ComingInM3 : null));

    private void RegisterCommands()
    {
        _newCommand = new AsyncRelayCommand(NewDocumentAsync, () => _workflow is not null);
        _openCommand = new AsyncRelayCommand(OpenFromDialogAsync, () => _workflow is not null);
        _saveCommand = new AsyncRelayCommand(SaveActiveAsync, () => _workflow is not null && ActiveDocument?.Session is not null);
        _saveAsCommand = new AsyncRelayCommand(SaveActiveAsAsync, () => _workflow is not null && ActiveDocument?.Session is not null);
        _closeActiveCommand = new AsyncRelayCommand(() => ActiveDocument is { } d ? CloseDocumentAsync(d) : Task.CompletedTask,
            () => ActiveDocument is not null);
        Reg("file.new", Strings.Cmd_New, "note", G(Key.N), _newCommand);
        Reg("file.open", Strings.Cmd_Open, "open", G(Key.O), _openCommand);
        Reg("file.save", Strings.Cmd_Save, "save", G(Key.S), _saveCommand);
        Reg("file.saveAs", Strings.Cmd_SaveAs, "save", G(Key.S, KeyModifiers.Control | KeyModifiers.Shift), _saveAsCommand);
        _printCommand = new AsyncRelayCommand(PrintActiveAsync, CanPrintActive);
        Reg("file.print", Strings.Cmd_Print, "print", G(Key.P), _printCommand);
        Reg("file.prefs", Strings.Cmd_Preferences, null, null, new RelayCommand(() => PreferencesRequested?.Invoke(this, EventArgs.Empty)));
        Reg("file.about", Strings.Cmd_About, null, null, new RelayCommand(() => AboutRequested?.Invoke(this, EventArgs.Empty)));
        Reg("help.show", Strings.Cmd_Help, "help", null, new RelayCommand(() => HelpRequested?.Invoke(this, EventArgs.Empty)));
        Reg("help.about", Strings.Cmd_About, "info", null, new RelayCommand(() => AboutRequested?.Invoke(this, EventArgs.Empty)));
        Reg("file.exit", Strings.Cmd_Exit, null, null, new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty)));
        Reg("edit.undo", Strings.Cmd_Undo, "undo", G(Key.Z), PageCmd(p => p.UndoCommand));
        Reg("edit.redo", Strings.Cmd_Redo, "redo", G(Key.Y), PageCmd(p => p.RedoCommand));
        Reg("doc.close", Strings.Cmd_CloseDoc, null, G(Key.W), _closeActiveCommand);
        Reg("find", Strings.Cmd_Find, "search", G(Key.F), DocCmd(d => { ActiveLeftPanel = PanelKind.Search; IsLeftPanelVisible = true; FocusSearchRequested?.Invoke(this, EventArgs.Empty); }));
        Reg("find.replace", Strings.Cmd_FindReplace, "findreplace", G(Key.H), DocCmd(d => OpenReplacePanel(d)));
        Reg("find.next", Strings.Cmd_FindNext, null, new KeyGesture(Key.F3, KeyModifiers.None), DocCmd(d => d.Search?.NextCommand.Execute(null)));
        Reg("find.prev", Strings.Cmd_FindPrev, null, new KeyGesture(Key.F3, KeyModifiers.Shift), DocCmd(d => d.Search?.PrevCommand.Execute(null)));

        Reg("tool.hand", Strings.Cmd_Hand, "hand", null, DocCmd(d => d.Tool = ToolKind.Hand));
        Reg("tool.textSelect", Strings.Cmd_TextSelect, "tsel", null, DocCmd(d => d.Tool = ToolKind.TextSelect));
        Reg("tool.snapshot", Strings.Cmd_Snapshot, "snapshot", null, DocCmd(d => d.Tool = ToolKind.Snapshot));
        Reg("zoom.in", Strings.Cmd_ZoomIn, "zin", G(Key.OemPlus), DocCmd(d => d.ZoomIn()));
        Reg("zoom.out", Strings.Cmd_ZoomOut, "zout", G(Key.OemMinus), DocCmd(d => d.ZoomOut()));
        Reg("zoom.actual", Strings.Cmd_ActualSize, "actual", G(Key.D1), DocCmd(d => d.ActualSize()));
        Reg("zoom.fitWidth", Strings.Cmd_FitWidth, "fitwidth", G(Key.D2), DocCmd(d => d.FitWidth()));
        Reg("zoom.fitPage", Strings.Cmd_FitPage, "fitpage", G(Key.D0), DocCmd(d => d.FitPage()));
        Reg("page.rotateLeft", Strings.Cmd_PageRotateLeft, "rotateleft", null, PageCmd(p => p.RotateLeftCommand));
        Reg("page.rotateRight", Strings.Cmd_PageRotateRight, "rotate", null, PageCmd(p => p.RotateRightCommand));

        Reg("annot.select", Strings.Cmd_AnnotSelect, "select", null, ToolCmd(ToolKind.AnnotSelect));
        Reg("annot.typewriter", Strings.Cmd_Typewriter, "type", null, ToolCmd(ToolKind.Typewriter));
        Reg("annot.textbox", Strings.Cmd_TextBox, "text", null, ToolCmd(ToolKind.TextBox));
        Reg("annot.callout", Strings.Cmd_Callout, "callout", null, ToolCmd(ToolKind.Callout));
        Reg("annot.sticky", Strings.Cmd_StickyNote, "note", null, ToolCmd(ToolKind.StickyNote));
        Reg("annot.attach", Strings.Cmd_FileAttach, "clip", null, ToolCmd(ToolKind.FileAttach));
        Reg("annot.hlText", Strings.Cmd_HighlightText, "hl", null, ToolCmd(ToolKind.HighlightText));
        Reg("annot.hlArea", Strings.Cmd_HighlightArea, "area", null, ToolCmd(ToolKind.HighlightArea));
        Reg("annot.underline", Strings.Cmd_Underline, "under", null, ToolCmd(ToolKind.Underline));
        Reg("annot.strike", Strings.Cmd_Strikeout, "strike", null, ToolCmd(ToolKind.StrikeOut));
        Reg("annot.insertText", Strings.Cmd_InsertText, "caret", null, ToolCmd(ToolKind.InsertText));
        Reg("annot.replaceText", Strings.Cmd_ReplaceText, "replace", null, ToolCmd(ToolKind.ReplaceText));
        Reg("annot.line", Strings.Cmd_Line, "line", null, ToolCmd(ToolKind.Line));
        Reg("annot.arrow", Strings.Cmd_Arrow, "arrow", null, ToolCmd(ToolKind.Arrow));
        Reg("annot.rect", Strings.Cmd_Rectangle, "rect", null, ToolCmd(ToolKind.Rectangle));
        Reg("annot.ellipse", Strings.Cmd_Ellipse, "oval", null, ToolCmd(ToolKind.Ellipse));
        Reg("annot.polygon", Strings.Cmd_Polygon, "poly", null, ToolCmd(ToolKind.Polygon));
        Reg("annot.polyline", Strings.Cmd_Polyline, "pline", null, ToolCmd(ToolKind.PolyLine));
        Reg("annot.cloud", Strings.Cmd_Cloud, "cloud", null, ToolCmd(ToolKind.Cloud));
        Reg("annot.pencil", Strings.Cmd_Pencil, "pen", null, ToolCmd(ToolKind.Pencil));
        Reg("annot.eraser", Strings.Cmd_Eraser, "eraser", null, ToolCmd(ToolKind.Eraser));
        Reg("annot.stamp", Strings.Cmd_Stamp, "stamp", null, ToolCmd(ToolKind.Stamp));
        Reg("annot.more", Strings.Cmd_More, "more", null, DocCmd(_ => { }));
        Reg("annot.commentList", Strings.Cmd_CommentList, "comments", null,
            DocCmd(_ => { ActiveLeftPanel = PanelKind.Comments; IsLeftPanelVisible = true; }));
        Reg("annot.summary", Strings.Cmd_AnnotSummary, "layers", null, DocCmd(d => _ = SummaryAsync(d)));
        Reg("annot.importExport", Strings.Cmd_ImportExport, "share");
        Reg("annot.importXfdf", Strings.Cmd_ImportXfdf, "share", null, DocCmd(d => _ = ImportXfdfAsync(d)));
        Reg("annot.exportXfdf", Strings.Cmd_ExportXfdf, "share", null, DocCmd(d => _ = ExportXfdfAsync(d)));
        Reg("annot.style", Strings.Cmd_AnnotStyle, "pen");

        Reg("edit.text", Strings.Cmd_EditText, "textedit", null, ToolCmd(ToolKind.EditText));
        Reg("edit.object", Strings.Cmd_EditObject, "objedit", null, ToolCmd(ToolKind.EditObject));
        Reg("edit.addText", Strings.Cmd_AddText, "type", null, ToolCmd(ToolKind.AddText));
        Reg("edit.oneLineOnly", Strings.Cmd_OneLineOnly, "textedit", null, DocCmd(_ => OneLineOnly = !OneLineOnly));
        _addImageCommand = new AsyncRelayCommand(AddImageAsync, () => ActiveDocument is { Session: not null, CanEditContent: true });
        _docCommands.Add(_addImageCommand);
        Reg("edit.addImage", Strings.Cmd_AddImage, "image", null, _addImageCommand);
        Reg("edit.addShape", Strings.Cmd_AddShape, "rect", null, ToolCmd(ToolKind.AddShape));
        Reg("edit.shapeRect", Strings.Cmd_ShapeRect, "rect", null, ToolCmd(ToolKind.AddShape));
        Reg("edit.shapeEllipse", Strings.Cmd_ShapeEllipse, "oval", null, ToolCmd(ToolKind.AddEllipse));
        Reg("edit.shapeLine", Strings.Cmd_ShapeLine, "line", null, ToolCmd(ToolKind.AddLine));

        Reg("page.insertBlank", Strings.Cmd_PageInsertBlank, "pageadd", null, PageCmd(p => p.InsertBlankCommand));
        Reg("page.insertFile", Strings.Cmd_PageInsertFromFile, "insertfile", null, PageCmd(p => p.InsertFromFileCommand));
        Reg("page.delete", Strings.Cmd_PageDelete, "delete", null, PageCmd(p => p.DeleteCommand));
        Reg("page.moveForward", Strings.Cmd_PageMoveForward, "moveup", null, PageCmd(p => p.MoveUpCommand));
        Reg("page.moveBack", Strings.Cmd_PageMoveBack, "movedown", null, PageCmd(p => p.MoveDownCommand));
        Reg("page.moveTo", Strings.Cmd_PageMoveTo, "moveto", null, PageCmd(p => p.MoveToCommand));
        Reg("page.merge", Strings.Cmd_MergeDocuments, "merge", null, PageCmd(p => p.MergeCommand));

        Reg("view.continuous", Strings.Cmd_Continuous, "continuous", null, DocCmd(d => d.ViewMode = ViewMode.Continuous));
        Reg("view.single", Strings.Cmd_SinglePage, "single", null, DocCmd(d => d.ViewMode = ViewMode.SinglePage));
        Reg("view.leftPanel", Strings.Cmd_LeftPanel, "side", null,
            new RelayCommand(() => IsLeftPanelVisible = !IsLeftPanelVisible));
        Reg("view.properties", Strings.Cmd_PropertiesPanel, "insp", null,
            new RelayCommand(() => IsPropertiesPanelVisible = !IsPropertiesPanelVisible));
        Reg("view.fullscreen", Strings.Cmd_FullScreen, "fullscreen", new KeyGesture(Key.F11),
            new RelayCommand(() => IsFullScreen = !IsFullScreen));
    }

    // ---- 리본 정의 ----
    private static IReadOnlyDictionary<string, IReadOnlyList<RibbonGroupModel>> BuildRibbon()
    {
        string tbd = Strings.Msg_NotYetConnected;
        RibbonItemModel I(string id, string label, string? tip = null) => new(id, label, tip);
        RibbonItemModel T(string id, string label, ToolKind tool, string? tip = null) => new(id, label, tip, tool);
        RibbonGroupModel Gr(string name, bool compact, params RibbonItemModel[] items) => new(name, items, compact);
        static RibbonItemModel L(RibbonItemModel i) => i with { Large = true };

        return new Dictionary<string, IReadOnlyList<RibbonGroupModel>>
        {
            // 2026-10-20 사용자 결정: 다른 프로그램의 리본을 참고만 하고 똑같이 만들지 않는다. 묶음마다 대표 도구 하나를 큰 단추로,
            // 나머지는 작은 단추로 두는 SedolPDF 고유 배치(묶음 이름·순서도 자체 구성).
            ["Home"] =
            [
                Gr(Strings.Group_Select, true, L(T("tool.hand", Strings.Cmd_Hand, ToolKind.Hand)),
                    T("tool.textSelect", Strings.Cmd_TextSelect, ToolKind.TextSelect, Strings.Tip_TextSelect), T("annot.select", Strings.Cmd_AnnotSelect, ToolKind.AnnotSelect)),
                Gr(Strings.Group_Capture, false, T("tool.snapshot", Strings.Cmd_Snapshot, ToolKind.Snapshot, Strings.Tip_Snapshot)),
                Gr(Strings.Group_Zoom, true, I("zoom.in", Strings.Cmd_ZoomIn), I("zoom.out", Strings.Cmd_ZoomOut), I("zoom.actual", Strings.Cmd_ActualSize),
                    I("zoom.fitWidth", Strings.Cmd_FitWidth), I("zoom.fitPage", Strings.Cmd_FitPage)),
                Gr(Strings.Group_Rotate, true, I("page.rotateLeft", Strings.Cmd_RotateLeft), I("page.rotateRight", Strings.Cmd_RotateRight)),
                Gr(Strings.Group_Find, false, I("find", Strings.Cmd_Find), I("find.replace", Strings.Cmd_FindReplace, Strings.Tip_FindReplace)),
                Gr(Strings.Group_EditTools, true, L(T("edit.text", Strings.Cmd_EditText, ToolKind.EditText)), T("edit.object", Strings.Cmd_EditObject, ToolKind.EditObject),
                    T("edit.addText", Strings.Cmd_AddText, ToolKind.AddText), I("edit.addImage", Strings.Cmd_AddImage, Strings.Tip_AddImage)),
                Gr(Strings.Group_QuickAnnot, true, T("annot.typewriter", Strings.Cmd_Typewriter, ToolKind.Typewriter),
                    T("annot.hlText", Strings.Cmd_HighlightText, ToolKind.HighlightText), T("annot.underline", Strings.Cmd_Underline, ToolKind.Underline),
                    T("annot.sticky", Strings.Cmd_StickyNote, ToolKind.StickyNote), T("annot.arrow", Strings.Cmd_Arrow, ToolKind.Arrow),
                    T("annot.rect", Strings.Cmd_Rectangle, ToolKind.Rectangle), L(T("annot.stamp", Strings.AnnotKind_Stamp, ToolKind.Stamp))),
            ],
            ["Annotate"] =
            [
                // "더보기" 없이 모든 만들기 도구가 보인다(2026-10-20). 도형은 그림만 있는 격자(이름은 툴팁).
                Gr(Strings.Group_Select, true, L(T("tool.hand", Strings.Cmd_Hand, ToolKind.Hand)),
                    T("tool.textSelect", Strings.Cmd_TextSelect, ToolKind.TextSelect, Strings.Tip_TextSelect), T("annot.select", Strings.Cmd_AnnotSelect, ToolKind.AnnotSelect)),
                Gr(Strings.Group_Writing, true, L(T("annot.textbox", Strings.Cmd_TextBox, ToolKind.TextBox)),
                    T("annot.typewriter", Strings.Cmd_Typewriter, ToolKind.Typewriter), T("annot.callout", Strings.Cmd_Callout, ToolKind.Callout)),
                Gr(Strings.Group_Emphasis, true, L(T("annot.hlText", Strings.Cmd_HighlightText, ToolKind.HighlightText)),
                    T("annot.hlArea", Strings.Cmd_HighlightArea, ToolKind.HighlightArea), T("annot.underline", Strings.Cmd_Underline, ToolKind.Underline),
                    T("annot.strike", Strings.Cmd_Strikeout, ToolKind.StrikeOut), T("annot.insertText", Strings.Cmd_InsertText, ToolKind.InsertText),
                    T("annot.replaceText", Strings.Cmd_ReplaceText, ToolKind.ReplaceText)),
                Gr(Strings.Group_Draw, true, L(T("annot.pencil", Strings.Cmd_Pencil, ToolKind.Pencil)), T("annot.eraser", Strings.Cmd_Eraser, ToolKind.Eraser)),
                new(Strings.Group_Shapes,
                [
                    T("annot.rect", Strings.Cmd_Rectangle, ToolKind.Rectangle), T("annot.ellipse", Strings.Cmd_Ellipse, ToolKind.Ellipse),
                    T("annot.cloud", Strings.Cmd_Cloud, ToolKind.Cloud), T("annot.line", Strings.Cmd_Line, ToolKind.Line),
                    T("annot.arrow", Strings.Cmd_Arrow, ToolKind.Arrow), T("annot.polyline", Strings.Cmd_Polyline, ToolKind.PolyLine),
                    T("annot.polygon", Strings.Cmd_Polygon, ToolKind.Polygon),
                ], Compact: true, IconOnly: true),
                Gr(Strings.Group_Marks, true, L(T("annot.stamp", Strings.AnnotKind_Stamp, ToolKind.Stamp)),
                    T("annot.sticky", Strings.Cmd_StickyNote, ToolKind.StickyNote), T("annot.attach", Strings.Cmd_FileAttach, ToolKind.FileAttach)),
                Gr(Strings.Group_AnnotManage, true, L(I("annot.commentList", Strings.Cmd_CommentList)), I("annot.summary", Strings.Cmd_AnnotSummary),
                    I("annot.importXfdf", Strings.Cmd_ImportXfdf, Strings.Tip_ImportXfdf), I("annot.exportXfdf", Strings.Cmd_ExportXfdf, Strings.Tip_ExportXfdf)),
            ],
            ["Edit"] =
            [
                Gr(Strings.Group_EditTools, false, T("edit.text", Strings.Cmd_EditText, ToolKind.EditText), T("edit.object", Strings.Cmd_EditObject, ToolKind.EditObject),
                    I("edit.oneLineOnly", Strings.Cmd_OneLineOnly, Strings.Tip_OneLineOnly),
                    T("edit.addText", Strings.Cmd_AddText, ToolKind.AddText), I("edit.addImage", Strings.Cmd_AddImage, Strings.Tip_AddImage)),
                Gr(Strings.Group_AddShapes, false, T("edit.shapeRect", Strings.Cmd_ShapeRect, ToolKind.AddShape), T("edit.shapeEllipse", Strings.Cmd_ShapeEllipse, ToolKind.AddEllipse),
                    T("edit.shapeLine", Strings.Cmd_ShapeLine, ToolKind.AddLine)),
            ],
            ["Page"] =
            [
                Gr(Strings.Group_Insert, false, I("page.insertBlank", Strings.Cmd_InsertBlank), I("page.insertFile", Strings.Cmd_InsertFromFile)),
                Gr(Strings.Group_Delete, false, I("page.delete", Strings.Cmd_DeletePage)),
                Gr(Strings.Group_Rotate, false, I("page.rotateLeft", Strings.Cmd_RotateLeft), I("page.rotateRight", Strings.Cmd_RotateRight)),
                Gr(Strings.Group_Order, false, I("page.moveForward", Strings.Cmd_MoveForward), I("page.moveBack", Strings.Cmd_MoveBack), I("page.moveTo", Strings.Cmd_MoveTo)),
                Gr(Strings.Group_Document, false, I("page.merge", Strings.Cmd_Merge)),
            ],
            ["View"] =
            [
                Gr(Strings.Group_PageView, false, I("view.continuous", Strings.Cmd_Continuous, tbd), I("view.single", Strings.Cmd_SinglePage, tbd)),
                Gr(Strings.Group_Panels, false, I("view.leftPanel", Strings.Cmd_LeftPanel), I("view.properties", Strings.Cmd_PropertiesPanel)),
                Gr(Strings.Group_Screen, false, I("view.fullscreen", Strings.Cmd_FullScreen)),
            ],
            ["Help"] =
            [
                Gr(Strings.Group_HelpInfo, false, I("help.show", Strings.Cmd_Help, Strings.Tip_Help), I("help.about", Strings.Cmd_About, Strings.Tip_About)),
            ],
        };
    }
}
