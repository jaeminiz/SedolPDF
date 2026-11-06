using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using System.IO;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using PdfEditor.App.Dialogs;
using PdfEditor.App.Hosting;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Shell;

public sealed partial class MainWindow : Window, IHostWindow
{
    private readonly MainWindowViewModel _vm = new();
    public MainWindowViewModel ViewModel => _vm;
    public bool IsClosed { get; private set; }
    public PixelRect? TabStripScreenRect => null;   // 단계 4(끌어내기)에서 채운다

    /// <summary>최소화돼 있으면 되살리고 앞으로 가져온다.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    private readonly AppHost _host;   // 앱 공유 서비스(PDFium 스레드·설정·파일 작업·복구)
    private bool _exitConfirmed;
    private MenuItem _recentMenu = null!;

    /// <summary>이미지 추가: PNG·JPEG 고르기. 너무 큰 파일은 읽지 않고 빈 배열로 넘겨 보관소의 검증 실패(안내)를 탄다.</summary>
    private async Task<byte[]?> PickImageBytesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Dlg_PickImageTitle,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Strings.Dlg_FilterImages) { Patterns = ["*.png", "*.jpg", "*.jpeg"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return null;
        return new FileInfo(path).Length > PdfEditor.Document.Annotations.AnnotationAssets.MaxImageBytes ? [] : await File.ReadAllBytesAsync(path);
    }

    private async Task ShowPreferencesAsync()
    {
        var cur = _host.Settings.Load();
        var result = await new SettingsDialog(cur.RecoveryIntervalMinutes, cur.AuthorName, cur.Language).ShowDialog<SettingsResult?>(this);
        if (result is null) return;
        try { _host.Settings.Update(s => s with { RecoveryIntervalMinutes = result.RecoveryMinutes, AuthorName = result.AuthorName, Language = result.Language }); }
        catch (Exception) { return; }
        _host.Recovery.Reschedule();
        if (result.Language != cur.Language) await new MessageDialog(Strings.Dlg_InfoTitle, Strings.Msg_LanguageRestart, [Strings.Dlg_Ok]).ShowDialog(this);
    }

    public MainWindow() : this(null) { }

    /// <param name="host">앱 공유 서비스. null이면 이 창이 자기 호스트를 만든다(테스트에서 창만 띄울 때).</param>
    public MainWindow(AppHost? host)
    {
        _host = host ?? new AppHost(AppContext.BaseDirectory);
        _host.Registry.Add(this);
        Activated += (_, _) => _host.Registry.MarkActivated(this);
        Closed += (_, _) =>
        {
            IsClosed = true;
            _host.Registry.Remove(this);
            if (_host.Registry.Count == 0) _host.Dispose();   // 마지막 창이 닫히면 앱 공유 서비스도 끝낸다
        };
        InitializeComponent();
        DataContext = _vm;
        BuildKeyBindings();
        WireTabDrag();
        LeftPanelSplitter.DragDelta += (_, dx) => _vm.SetLeftPanelWidth(_vm.LeftPanelWidth + dx);
        LeftPanelSplitter.Reset += (_, _) => _vm.ResetLeftPanelWidth();
        PropertiesPanelSplitter.DragDelta += (_, dx) => _vm.SetPropertiesPanelWidth(_vm.PropertiesPanelWidth - dx);
        PropertiesPanelSplitter.Reset += (_, _) => _vm.ResetPropertiesPanelWidth();
        TabStripPanel.ScrollStateChanged += _ => UpdateTabScrollButtons();
        FileTabButton.Flyout = BuildFileMenu();
        var prompts = _host.Prompts;
        var services = _host.Services;
        var appearance = services.Appearance;
        var patcher = services.Patcher;
        var settings = _host.Settings;
        try { var saved = settings.Load(); _vm.ApplyPanelState(saved); _vm.ApplyPanelWidths(saved); } catch (Exception) { /* 읽지 못하면 기본 배치 */ }
        _vm.PanelWidthsChanged += (_, _) =>
        {
            try { settings.Update(s => s with { LeftPanelWidths = _vm.LeftPanelWidths.ToDictionary(k => k.Key.ToString(), k => k.Value), PropertiesPanelWidth = _vm.SavedPropertiesPanelWidth }); }
            catch (Exception) { /* 저장 실패가 화면 조작을 막지 않는다 */ }
        };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(MainWindowViewModel.IsLeftPanelVisible) or nameof(MainWindowViewModel.ActiveLeftPanel)
                or nameof(MainWindowViewModel.IsPropertiesPanelVisible))) return;
            try
            {
                settings.Update(s => s with
                {
                    LeftPanelVisible = _vm.IsLeftPanelVisible, LeftPanelKind = _vm.ActiveLeftPanel.ToString(),
                    PropertiesPanelVisible = _vm.IsPropertiesPanelVisible,
                });
            }
            catch (Exception) { /* 저장 실패가 화면 조작을 막지 않는다 */ }
        };
        var workflow = _host.Workflow;
        _vm.AttachWorkflow(workflow, prompts);
        _vm.AttachPrinting(new Printing.AvaloniaPrintService(this, prompts, appearance, patcher, services.Assets));
        _vm.AttachReduceSize(new ReduceSizeWorkflow(new AvaloniaReduceSizeDialogs(this), prompts,
            new PdfEditor.Optimize.SizeReducer(new PdfEditor.Document.Output.OutputCopyBuilder(appearance, patcher, services.Assets))));
        _vm.PickImageBytes = PickImageBytesAsync;
        _vm.PreferencesRequested += async (_, _) => await ShowPreferencesAsync();
        _vm.AboutRequested += async (_, _) => await new AboutDialog().ShowDialog(this);
        _vm.HelpRequested += async (_, _) => await new HelpDialog().ShowDialog(this);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        _vm.NewWindowRequested += (_, _) => _host.CreateWindow(this);
        _vm.ExitRequested += async (_, _) => await _host.ExitAllAsync();
        // 마지막 탭을 닫아 빈 창이 되면: 다른 창이 있으면 이 창도 닫는다. 창이 하나뿐이면 빈 화면으로 남는다(설계 4.4 A안).
        _vm.BecameEmpty += (_, _) => { if (_host.Registry.Count > 1) Close(); };
        // 패널이 막 보이게 된 직후라서 레이아웃 뒤에 초점을 준다.
        _vm.FocusSearchRequested += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(SearchPanelView.FocusQuery,
            Avalonia.Threading.DispatcherPriority.Input);
        _vm.FocusReplaceRequested += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(ReplacePanelView.FocusFind,
            Avalonia.Threading.DispatcherPriority.Input);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsFullScreen))
                WindowState = _vm.IsFullScreen ? WindowState.FullScreen : WindowState.Normal;
        };
    }

    // Mica가 실제로 적용되면 배경을 투명하게 해서 보이게 하고, 아니면 단색 #F3F3F3을 유지한다. (검증 필요: Windows 11 실제 화면)
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActualTransparencyLevelProperty)
            Background = ActualTransparencyLevel == WindowTransparencyLevel.Mica ? Brushes.Transparent : new SolidColorBrush(Color.Parse("#F3F3F3"));
    }

    private void BuildKeyBindings()
    {
        foreach (var c in _vm.Commands.All.Where(c => c.Gesture is not null))
            KeyBindings.Add(new KeyBinding { Gesture = c.Gesture!, Command = c.Command });
        // 같은 명령의 보조 단축키(널리 쓰이는 PDF 뷰어 관례): 단축키 자체는 기능 관례라 저작권과 무관하지만 화면·문구는 따로 정한다
        void Alt(string id, Key key, KeyModifiers mods)
        {
            if (_vm.Commands.Find(id) is { } cmd) KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, mods), Command = cmd.Command });
        }
        Alt("edit.redo", Key.Z, KeyModifiers.Control | KeyModifiers.Shift);
        Alt("find.next", Key.G, KeyModifiers.Control);
        Alt("find.prev", Key.G, KeyModifiers.Control | KeyModifiers.Shift);
        Alt("file.exit", Key.Q, KeyModifiers.Control);
        Alt("view.fullscreen", Key.L, KeyModifiers.Control);
        Alt("zoom.in", Key.Add, KeyModifiers.Control);
        Alt("zoom.out", Key.Subtract, KeyModifiers.Control);
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.OemPeriod, KeyModifiers.Control),
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(TitleBarView.FocusQuickBox),
        });
    }

    private MenuFlyout BuildFileMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        MenuItem Item(string id)
        {
            var c = _vm.Commands.Find(id)!;
            return new MenuItem { Header = c.Label, Command = c.Command, InputGesture = c.Gesture };
        }

        flyout.Items.Add(Item("file.new"));
        flyout.Items.Add(Item("file.open"));
        flyout.Items.Add(Item("file.newWindow"));
        _recentMenu = new MenuItem { Header = Strings.Cmd_RecentDocs };
        flyout.Items.Add(_recentMenu);
        flyout.Opening += (_, _) => RefreshRecentMenu();
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("file.save"));
        flyout.Items.Add(Item("file.saveAs"));
        var saveOther = new MenuItem { Header = Strings.Cmd_SaveOther };
        saveOther.Items.Add(Item("file.reduceSize"));
        flyout.Items.Add(saveOther);
        flyout.Items.Add(new Separator());
        var printItem = Item("file.print");
        ToolTip.SetShowOnDisabled(printItem, true);
        flyout.Opening += (_, _) => ToolTip.SetTip(printItem, _vm.PrintDisabledTip);
        flyout.Items.Add(printItem);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("file.prefs"));
        flyout.Items.Add(Item("file.about"));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("file.exit"));
        return flyout;
    }

    // 파일 메뉴를 열 때마다 최근 문서 목록을 다시 만든다.
    private void RefreshRecentMenu()
    {
        _recentMenu.Items.Clear();
        var recent = _vm.RecentFiles;
        if (recent.Count == 0)
        {
            _recentMenu.Items.Add(new MenuItem { Header = Strings.Cmd_NoRecent, IsEnabled = false });
            return;
        }
        foreach (var path in recent)
        {
            var p = path;
            var item = new MenuItem { Header = Path.GetFileName(p) };
            ToolTip.SetTip(item, p);
            item.Click += async (_, _) => await _vm.OpenAsync(p);
            _recentMenu.Items.Add(item);
        }
    }

    private static IEnumerable<string> PdfPathsOf(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath())
            .Where(p => p is not null && p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).Select(p => p!);

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = PdfPathsOf(e).Any() ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = PdfPathsOf(e).ToList();
        if (paths.Count > 0) await _vm.OpenManyAsync(paths);
    }

    // 닫기: 열린 탭마다 저장 여부를 묻고, 하나라도 취소하면 닫지 않는다.
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_exitConfirmed) return;
        e.Cancel = true;
        await TryCloseAsync();
    }

    private Task<bool>? _closeTask;

    /// <summary>이 창의 탭마다 저장 여부를 묻고, 다 닫히면 창을 닫고 true. 하나라도 취소하면 창은 그대로 false. 묻는 중에 또 부르면 같은 결과를 기다린다.</summary>
    public Task<bool> TryCloseAsync()
    {
        if (IsClosed) return Task.FromResult(true);
        if (_closeTask is { IsCompleted: false } running) return running;
        return _closeTask = TryCloseCoreAsync();
    }

    private async Task<bool> TryCloseCoreAsync()
    {
        if (!await _vm.ConfirmExitAsync()) return false;
        _exitConfirmed = true;
        Close();
        return true;
    }

    private void OnFormatTabClick(object? sender, RoutedEventArgs e) => _vm.SelectRibbonTabCommand.Execute(MainWindowViewModel.FormatTabId);

    private void OnRibbonTabClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RibbonTabVm tab) _vm.SelectRibbonTabCommand.Execute(tab.Id);
    }

    // 패널 머리 더블클릭 = 접기(0.4.0, 결정 D4). 왼쪽 패널은 활동 아이콘을 다시 누르면 펼쳐진다.
    private void OnLeftPanelHeaderDoubleTapped(object? sender, TappedEventArgs e) => _vm.CloseLeftPanelCommand.Execute(null);
    private void OnPropertiesPanelHeaderDoubleTapped(object? sender, TappedEventArgs e) => _vm.ClosePropertiesPanelCommand.Execute(null);

    private void OnPanelButtonClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PanelButtonVm p) _vm.SetPanelCommand.Execute(p.Kind);
    }

    // ---- 문서 탭 끌어 순서 바꾸기 (2026-10-21 사용자 요청) ----

    private DocumentTabViewModel? _tabPressed;
    private Point _tabPressPoint;
    private bool _tabDragging;
    private int _tabSlot = -1;

    private void WireTabDrag()
    {
        DocTabs.AddHandler(PointerPressedEvent, OnTabPressed, RoutingStrategies.Tunnel);
        DocTabs.AddHandler(PointerMovedEvent, OnTabMoved, RoutingStrategies.Tunnel);
        DocTabs.AddHandler(PointerReleasedEvent, OnTabReleased, RoutingStrategies.Tunnel);
        DocTabs.AddHandler(PointerCaptureLostEvent, (_, _) => EndTabDrag(), RoutingStrategies.Tunnel);
        DocTabs.AddHandler(PointerReleasedEvent, OnTabMiddleClick, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || !_tabDragging) return;
            EndTabDrag();   // 취소. 손을 떼면 아무 것도 하지 않는다.
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>탭 제목을 휠(가운데) 단추로 누르면 그 탭을 닫는다(2026-10-21 사용자 요청).</summary>
    private void OnTabMiddleClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle) return;
        var item = (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is not DocumentTabViewModel d) return;
        e.Handled = true;
        _vm.CloseDocumentCommand.Execute(d);
    }

    private TabStripPanel? TabPanel => DocTabs.GetVisualDescendants().OfType<TabStripPanel>().FirstOrDefault();

    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        EndTabDrag();
        if (!e.GetCurrentPoint(DocTabs).Properties.IsLeftButtonPressed) return;
        var src = e.Source as Avalonia.Visual;
        if (src?.FindAncestorOfType<Button>(includeSelf: true) is not null) return;   // 닫기 단추는 끌기가 아니다
        if (src?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not DocumentTabViewModel d) return;
        _tabPressed = d;
        _tabPressPoint = e.GetPosition(DocTabs);
    }

    private void OnTabMoved(object? sender, PointerEventArgs e)
    {
        if (_tabPressed is null || TabPanel is not { } panel) return;
        if (!_tabDragging)
        {
            if (!e.GetCurrentPoint(DocTabs).Properties.IsLeftButtonPressed) { EndTabDrag(); return; }
            if (Math.Abs(e.GetPosition(DocTabs).X - _tabPressPoint.X) < 6) return;
            _tabDragging = true;
            e.Pointer.Capture(DocTabs);
        }
        e.Handled = true;
        var tabs = panel.TabBounds();
        _tabSlot = TabReorder.Slot(tabs, e.GetPosition(panel).X);
        double x = tabs.Count == 0 ? 0 : _tabSlot < tabs.Count ? tabs[_tabSlot].Left : tabs[^1].Left + tabs[^1].Width;
        var at = panel.TranslatePoint(new Point(x, 0), (Visual)DocTabs.Parent!) ?? default;
        TabDropLine.Margin = new Thickness(Math.Max(0, at.X - 1.5), 0, 0, 3);
        TabDropLine.IsVisible = true;
    }

    private void OnTabReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_tabDragging) { _tabPressed = null; return; }
        var tab = _tabPressed;
        int slot = _tabSlot;
        EndTabDrag();
        e.Handled = true;
        if (tab is not null && slot >= 0) _vm.MoveDocument(tab, slot);
    }

    private void EndTabDrag()
    {
        _tabDragging = false;
        _tabPressed = null;
        _tabSlot = -1;
        TabDropLine.IsVisible = false;
    }

    /// <summary>탭 줄이 넘칠 때만 왼쪽·오른쪽 스크롤 단추를 보이고, 끝에 닿은 쪽은 사용하지 않게 한다.</summary>
    private void UpdateTabScrollButtons()
    {
        if (TabPanel is not { } p) return;
        TabScrollLeft.IsVisible = TabScrollRight.IsVisible = p.IsOverflowing;
        TabScrollLeft.IsEnabled = p.CanScrollLeft;
        TabScrollRight.IsEnabled = p.CanScrollRight;
    }

    private void OnTabScrollLeft(object? sender, RoutedEventArgs e) { if (TabPanel is { } p) p.ScrollBy(-p.PageStep); }
    private void OnTabScrollRight(object? sender, RoutedEventArgs e) { if (TabPanel is { } p) p.ScrollBy(p.PageStep); }

    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DocumentTabViewModel d) _vm.CloseDocumentCommand.Execute(d);
        e.Handled = true;
    }

    // ---- 탭 오른쪽 클릭 메뉴 ----

    /// <summary>탭 항목(글자 없는 빈 곳 포함) 어디서든 오른쪽 클릭하면 메뉴를 연다. 파일로 저장돼 있지 않거나 파일이 없어졌으면 "포함하는 폴더 열기"·"경로 복사"를 끈다.</summary>
    private void OnTabContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true) is not { DataContext: DocumentTabViewModel d } item) return;
        e.Handled = true;
        string? path = d.Session?.FilePath;
        bool has = FileReveal.BuildArguments(path) is not null;
        var reveal = new MenuItem { Header = Strings.TabMenu_RevealInFolder, IsEnabled = has };
        reveal.Click += (_, _) => FileReveal.Reveal(path);
        var copy = new MenuItem { Header = Strings.TabMenu_CopyPath, IsEnabled = has };
        copy.Click += async (_, _) => { if (path is { Length: > 0 } && Clipboard is { } cb) await cb.SetTextAsync(path); };
        var close = new MenuItem { Header = Strings.TabMenu_Close };
        close.Click += (_, _) => _vm.CloseDocumentCommand.Execute(d);
        new ContextMenu { ItemsSource = new object[] { reveal, copy, new Separator(), close } }.Open(item);
    }
}
