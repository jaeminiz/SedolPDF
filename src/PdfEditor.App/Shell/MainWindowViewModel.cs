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

namespace PdfEditor.App.Shell;

/// <summary>앱 셸의 상태. 문서 관련 명령은 자리 표시용(CanExecute false)이고 Task 11~17에서 연결한다.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private const double MinZoomPercent = 10, MaxZoomPercent = 1600;
    private static readonly double ZoomLogSpan = Math.Log(MaxZoomPercent / MinZoomPercent);
    private readonly Dictionary<string, IReadOnlyList<RibbonGroupVm>> _groupCache = new();
    private readonly List<IRelayCommand> _docCommands = [];
    private bool _syncingDocZoom;

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

    /// <summary>찾기 명령이 검색 패널을 열었다. 창이 검색어 입력칸에 초점을 준다.</summary>
    public event EventHandler? FocusSearchRequested;

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
        SelectRibbonTabCommand = new RelayCommand<string>(t => { if (t is not null) SelectedRibbonTab = t; });
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
            new("Page", Strings.Tab_Page), new("View", Strings.Tab_View),
        ];
        RibbonTabs[0].IsSelected = true;
        PanelButtons =
        [
            new(PanelKind.Thumbnails, Strings.Panel_Thumbnails, "thumbs"), new(PanelKind.Bookmarks, Strings.Panel_Bookmarks, "bookmark"),
            new(PanelKind.Comments, Strings.Panel_Comments, "comments"), new(PanelKind.Attachments, Strings.Panel_Attachments, "clip"),
            new(PanelKind.Layers, Strings.Panel_Layers, "layers"), new(PanelKind.Search, Strings.Panel_Search, "search"),
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
    public SearchViewModel? ActiveSearch => ActiveDocument?.Search;
    public BookmarksViewModel? ActiveBookmarks => ActiveDocument?.Bookmarks;
    public AttachmentsViewModel? ActiveAttachments => ActiveDocument?.Attachments;
    public LayersViewModel? ActiveLayers => ActiveDocument?.Layers;
    public PropertiesViewModel? ActiveProperties => ActiveDocument?.Properties;
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
        RefreshDocumentStatus();
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(HasNoDocument));
        OnPropertyChanged(nameof(PrintDisabledTip));
        NotifyFileCommands();
        foreach (var c in _docCommands) c.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ActiveThumbnails));
        OnPropertyChanged(nameof(ActiveBookmarks));
        OnPropertyChanged(nameof(ActiveAttachments));
        OnPropertyChanged(nameof(ActiveLayers));
        OnPropertyChanged(nameof(ActiveProperties));
        OnPropertyChanged(nameof(ActiveSearch));
        if (newValue is not null) SetZoomFromDocument(newValue.Scale);
        LoadVisiblePanels();
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
        var search = tab.Search = new SearchViewModel(tab);
        tab.NoticeRequested += msg => { _ = _prompts!.ShowInfoAsync(msg); };
        tab.Detached += () => { search.Clear(); thumbs.Dispose(); cmds.Dispose(); };
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

    private async Task ShowErrorAsync(Exception ex)
    {
        if (_prompts is not null) await _prompts.ShowErrorAsync(ErrorMessages.For(ex));
    }

    private void OnDocumentPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        RefreshDocumentStatus();
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
        OnPropertyChanged(nameof(ShowSearch));
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
        if (_groupCache.TryGetValue(tab, out var cached)) return cached;
        var groups = Ribbon[tab].Select(g => new RibbonGroupVm(g.Name,
            g.Items.Select(i => new RibbonButtonVm(Commands.Find(i.CommandId)!, i.Label, !g.Compact, i.DisabledTip)).ToList(),
            g.Compact)).ToList();
        return _groupCache[tab] = groups;
    }

    // ---- 명령 등록 ----
    private static RelayCommand Placeholder() => new(() => { }, () => false);
    private static KeyGesture G(Key k, KeyModifiers m = KeyModifiers.Control) => new(k, m);

    private void Reg(string id, string label, string? icon, KeyGesture? gesture = null, ICommand? command = null) =>
        Commands.Register(new AppCommand(id, label, command ?? Placeholder(), gesture, icon));

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
        Reg("file.about", Strings.Cmd_About, null);
        Reg("file.exit", Strings.Cmd_Exit, null, null, new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty)));
        Reg("edit.undo", Strings.Cmd_Undo, "undo", G(Key.Z), PageCmd(p => p.UndoCommand));
        Reg("edit.redo", Strings.Cmd_Redo, "redo", G(Key.Y), PageCmd(p => p.RedoCommand));
        Reg("doc.close", Strings.Cmd_CloseDoc, null, G(Key.W), _closeActiveCommand);
        Reg("find", Strings.Cmd_Find, "search", G(Key.F), DocCmd(d => { ActiveLeftPanel = PanelKind.Search; IsLeftPanelVisible = true; FocusSearchRequested?.Invoke(this, EventArgs.Empty); }));
        Reg("find.next", Strings.Cmd_FindNext, null, new KeyGesture(Key.F3, KeyModifiers.None), DocCmd(d => d.Search?.NextCommand.Execute(null)));
        Reg("find.prev", Strings.Cmd_FindPrev, null, new KeyGesture(Key.F3, KeyModifiers.Shift), DocCmd(d => d.Search?.PrevCommand.Execute(null)));

        Reg("tool.hand", Strings.Cmd_Hand, "hand", null, DocCmd(d => d.Tool = ToolKind.Hand));
        Reg("tool.textSelect", Strings.Cmd_TextSelect, "tsel", null, DocCmd(d => d.Tool = ToolKind.TextSelect));
        Reg("zoom.in", Strings.Cmd_ZoomIn, "zin", G(Key.OemPlus), DocCmd(d => d.ZoomIn()));
        Reg("zoom.out", Strings.Cmd_ZoomOut, "zout", G(Key.OemMinus), DocCmd(d => d.ZoomOut()));
        Reg("zoom.actual", Strings.Cmd_ActualSize, "actual", G(Key.D1), DocCmd(d => d.ActualSize()));
        Reg("zoom.fitWidth", Strings.Cmd_FitWidth, "fitwidth", G(Key.D2), DocCmd(d => d.FitWidth()));
        Reg("zoom.fitPage", Strings.Cmd_FitPage, "fitpage", G(Key.D0), DocCmd(d => d.FitPage()));
        Reg("page.rotateLeft", Strings.Cmd_PageRotateLeft, "rotateleft", null, PageCmd(p => p.RotateLeftCommand));
        Reg("page.rotateRight", Strings.Cmd_PageRotateRight, "rotate", null, PageCmd(p => p.RotateRightCommand));

        Reg("annot.select", Strings.Cmd_AnnotSelect, "select");
        Reg("annot.typewriter", Strings.Cmd_Typewriter, "type");
        Reg("annot.textbox", Strings.Cmd_TextBox, "text");
        Reg("annot.callout", Strings.Cmd_Callout, "callout");
        Reg("annot.sticky", Strings.Cmd_StickyNote, "note");
        Reg("annot.attach", Strings.Cmd_FileAttach, "clip");
        Reg("annot.hlText", Strings.Cmd_HighlightText, "hl");
        Reg("annot.hlArea", Strings.Cmd_HighlightArea, "area");
        Reg("annot.underline", Strings.Cmd_Underline, "under");
        Reg("annot.strike", Strings.Cmd_Strikeout, "strike");
        Reg("annot.insertText", Strings.Cmd_InsertText, "caret");
        Reg("annot.replaceText", Strings.Cmd_ReplaceText, "replace");
        Reg("annot.line", Strings.Cmd_Line, "line");
        Reg("annot.arrow", Strings.Cmd_Arrow, "arrow");
        Reg("annot.rect", Strings.Cmd_Rectangle, "rect");
        Reg("annot.ellipse", Strings.Cmd_Ellipse, "oval");
        Reg("annot.polygon", Strings.Cmd_Polygon, "poly");
        Reg("annot.polyline", Strings.Cmd_Polyline, "pline");
        Reg("annot.cloud", Strings.Cmd_Cloud, "cloud");
        Reg("annot.pencil", Strings.Cmd_Pencil, "pen");
        Reg("annot.eraser", Strings.Cmd_Eraser, "eraser");
        Reg("annot.stamp", Strings.Cmd_Stamp, "stamp");
        Reg("annot.commentList", Strings.Cmd_CommentList, "comments");
        Reg("annot.summary", Strings.Cmd_AnnotSummary, "layers");
        Reg("annot.importExport", Strings.Cmd_ImportExport, "share");
        Reg("annot.style", Strings.Cmd_AnnotStyle, "pen");

        Reg("edit.text", Strings.Cmd_EditText, "textedit");
        Reg("edit.object", Strings.Cmd_EditObject, "objedit");
        Reg("edit.addText", Strings.Cmd_AddText, "type");
        Reg("edit.addImage", Strings.Cmd_AddImage, "image");
        Reg("edit.addShape", Strings.Cmd_AddShape, "rect");

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
        Reg("view.fullscreen", Strings.Cmd_FullScreen, null, new KeyGesture(Key.F11),
            new RelayCommand(() => IsFullScreen = !IsFullScreen));
    }

    // ---- 리본 정의 ----
    private static IReadOnlyDictionary<string, IReadOnlyList<RibbonGroupModel>> BuildRibbon()
    {
        string m23 = Strings.Msg_ComingInM2M3, m45 = Strings.Msg_ComingInM4M5, tbd = Strings.Msg_NotYetConnected;
        RibbonItemModel I(string id, string label, string? tip = null) => new(id, label, tip);
        RibbonItemModel A(string id, string label) => new("annot." + id, label, m23);
        RibbonGroupModel Gr(string name, bool compact, params RibbonItemModel[] items) => new(name, items, compact);

        return new Dictionary<string, IReadOnlyList<RibbonGroupModel>>
        {
            ["Home"] =
            [
                Gr(Strings.Group_Tools, false, I("tool.hand", Strings.Cmd_Hand, tbd), I("tool.textSelect", Strings.Cmd_TextSelect, tbd)),
                Gr(Strings.Group_Zoom, false, I("zoom.in", Strings.Cmd_ZoomIn, tbd), I("zoom.out", Strings.Cmd_ZoomOut, tbd),
                    I("zoom.actual", Strings.Cmd_ActualSize, tbd), I("zoom.fitWidth", Strings.Cmd_FitWidth, tbd), I("zoom.fitPage", Strings.Cmd_FitPage, tbd)),
                Gr(Strings.Group_Rotate, false, I("page.rotateLeft", Strings.Cmd_RotateLeft), I("page.rotateRight", Strings.Cmd_RotateRight)),
                Gr(Strings.Group_Find, false, I("find", Strings.Cmd_Find)),
            ],
            ["Annotate"] =
            [
                Gr(Strings.Group_Tools, false, I("tool.hand", Strings.Cmd_Hand, m23), I("tool.textSelect", Strings.Cmd_TextSelect, m23), A("select", Strings.Cmd_AnnotSelect)),
                Gr(Strings.Group_Text, false, A("typewriter", Strings.Cmd_Typewriter), A("textbox", Strings.Cmd_TextBox), A("callout", Strings.Cmd_Callout)),
                Gr(Strings.Group_Note, false, A("sticky", Strings.Cmd_StickyNote), A("attach", Strings.Cmd_FileAttach)),
                Gr(Strings.Group_TextMarkup, true, A("hlText", Strings.Cmd_HighlightText), A("hlArea", Strings.Cmd_HighlightArea),
                    A("underline", Strings.Cmd_Underline), A("strike", Strings.Cmd_Strikeout), A("insertText", Strings.Cmd_InsertText), A("replaceText", Strings.Cmd_ReplaceText)),
                Gr(Strings.Group_Draw, true, A("line", Strings.Cmd_Line), A("arrow", Strings.Cmd_Arrow), A("rect", Strings.Cmd_Rectangle),
                    A("ellipse", Strings.Cmd_Ellipse), A("polygon", Strings.Cmd_Polygon), A("polyline", Strings.Cmd_Polyline),
                    A("cloud", Strings.Cmd_Cloud), A("pencil", Strings.Cmd_Pencil), A("eraser", Strings.Cmd_Eraser)),
                Gr(Strings.Group_Stamp, false, A("stamp", Strings.Cmd_Stamp)),
                Gr(Strings.Group_AnnotManage, true, A("commentList", Strings.Cmd_CommentList), A("summary", Strings.Cmd_AnnotSummary),
                    A("importExport", Strings.Cmd_ImportExport), A("style", Strings.Cmd_AnnotStyle)),
            ],
            ["Edit"] =
            [
                Gr(Strings.Group_EditTools, false, I("edit.text", Strings.Cmd_EditText, m45), I("edit.object", Strings.Cmd_EditObject, m45),
                    I("edit.addText", Strings.Cmd_AddText, m45), I("edit.addImage", Strings.Cmd_AddImage, m45), I("edit.addShape", Strings.Cmd_AddShape, m45)),
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
        };
    }
}
