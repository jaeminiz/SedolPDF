using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.IO;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Pdf.Native;
using PdfEditor.App.Dialogs;
using PdfEditor.App.Settings;
using PdfEditor.Document.Recovery;
using PdfEditor.Document.Saving;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Shell;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm = new();
    internal MainWindowViewModel ViewModel => _vm;
    private readonly PdfiumThread _pdfium = new();   // 앱 전체에서 PdfiumThread는 하나
    private bool _exitConfirmed;
    private MenuItem _recentMenu = null!;
    private readonly SettingsStore _settings;
    private readonly RecoveryCoordinator _recovery;

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
        var cur = _settings.Load();
        var result = await new SettingsDialog(cur.RecoveryIntervalMinutes, cur.AuthorName).ShowDialog<SettingsResult?>(this);
        if (result is null) return;
        try { _settings.Update(s => s with { RecoveryIntervalMinutes = result.RecoveryMinutes, AuthorName = result.AuthorName }); }
        catch (Exception) { return; }
        _recovery.Reschedule();
    }

    public MainWindow() : this([]) { }

    public MainWindow(IReadOnlyList<string> startupFiles)
    {
        InitializeComponent();
        DataContext = _vm;
        BuildKeyBindings();
        WireTabDrag();
        TabStripPanel.ScrollStateChanged += _ => UpdateTabScrollButtons();
        FileTabButton.Flyout = BuildFileMenu();
        var prompts = new AvaloniaPrompts(this);
        // 한글 글꼴(Noto Sans KR)은 앱 출력 폴더의 assets/fonts에서 한 번 읽어 AP·패치·화면이 함께 쓴다. 없으면 글꼴이 필요한 주석에서 예외.
        var services = AppearanceServices.Create(AppContext.BaseDirectory);
        PdfEditor.App.Documents.DocumentTabViewModel.Appearance = services;
        var appearance = services.Appearance;
        var patcher = services.Patcher;
        var paths = AppPaths.Resolve(AppContext.BaseDirectory, AppPaths.CanWriteFolder);
        _settings = new SettingsStore(paths.SettingsPath);
        PdfEditor.App.Documents.DocumentTabViewModel.Settings = _settings;
        var workflow = new FileWorkflow(_pdfium, new SaveService([], appearance, patcher, services.Assets), _settings, prompts);
        _vm.AttachWorkflow(workflow, prompts);
        _vm.AttachPrinting(new Printing.AvaloniaPrintService(this, prompts, appearance, patcher, services.Assets));
        _recovery = new RecoveryCoordinator(_pdfium, new RecoveryStore(paths.RecoveryFolder, appearance, patcher, services.Assets), _settings,
            new AvaloniaRecoveryUi(this), prompts);
        _recovery.Attach(workflow);
        _vm.PickImageBytes = PickImageBytesAsync;
        _vm.PreferencesRequested += async (_, _) => await ShowPreferencesAsync();
        _vm.AboutRequested += async (_, _) => await new AboutDialog().ShowDialog(this);
        _vm.HelpRequested += async (_, _) => await new HelpDialog().ShowDialog(this);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // 명령줄 인자로 받은 파일은 창이 뜬 뒤에 연다.
        // 먼저 이전에 남은 복구 항목을 묻고, 그다음 명령줄 파일을 연다(같은 파일이면 복구한 탭으로 전환됨).
        Opened += async (_, _) =>
        {
            if (paths.FellBackFromPortable)
            {
                try { await prompts.ShowInfoAsync(Strings.Msg_PortableFolderReadOnly); }
                catch (Exception) { /* 안내 실패가 시작을 막지 않는다 */ }
            }
            try
            {
                foreach (var s in await _recovery.RecoverAtStartupAsync()) _vm.ShowSession(s);
            }
            catch (Exception) { /* 복구 실패가 시작을 막지 않는다 */ }
            _recovery.Start(() => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => workflow.OpenSessions).GetTask());
            if (startupFiles.Count > 0) await _vm.OpenManyAsync(startupFiles);
        };

        _vm.ExitRequested += (_, _) => Close();
        // 패널이 막 보이게 된 직후라서 레이아웃 뒤에 초점을 준다.
        _vm.FocusSearchRequested += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(SearchPanelView.FocusQuery,
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
        _recentMenu = new MenuItem { Header = Strings.Cmd_RecentDocs };
        flyout.Items.Add(_recentMenu);
        flyout.Opening += (_, _) => RefreshRecentMenu();
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("file.save"));
        flyout.Items.Add(Item("file.saveAs"));
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
        if (await _vm.ConfirmExitAsync())
        {
            _exitConfirmed = true;
            _recovery.Dispose();
            _pdfium.Dispose();
            Close();
        }
    }

    private void OnFormatTabClick(object? sender, RoutedEventArgs e) => _vm.SelectRibbonTabCommand.Execute(MainWindowViewModel.FormatTabId);

    private void OnRibbonTabClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RibbonTabVm tab) _vm.SelectRibbonTabCommand.Execute(tab.Id);
    }

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
}
