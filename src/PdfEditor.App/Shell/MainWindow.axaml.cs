using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.IO;
using Avalonia.Media;
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
    private readonly PdfiumThread _pdfium = new();   // 앱 전체에서 PdfiumThread는 하나
    private bool _exitConfirmed;
    private MenuItem _recentMenu = null!;
    private readonly SettingsStore _settings;
    private readonly RecoveryCoordinator _recovery;

    private async Task ShowPreferencesAsync()
    {
        int? minutes = await new SettingsDialog(_settings.Load().RecoveryIntervalMinutes).ShowDialog<int?>(this);
        if (minutes is null) return;
        try { _settings.Save(_settings.Load() with { RecoveryIntervalMinutes = minutes.Value }); }
        catch (Exception) { return; }
        _recovery.Reschedule();
    }

    public MainWindow() : this([]) { }

    public MainWindow(IReadOnlyList<string> startupFiles)
    {
        InitializeComponent();
        DataContext = _vm;
        BuildKeyBindings();
        FileTabButton.Flyout = BuildFileMenu();

        var prompts = new AvaloniaPrompts(this);
        _settings = new SettingsStore(SettingsStore.DefaultPath);
        var workflow = new FileWorkflow(_pdfium, new SaveService([]), _settings, prompts);
        _vm.AttachWorkflow(workflow, prompts);
        _vm.AttachPrinting(new Printing.AvaloniaPrintService(this, prompts));
        _recovery = new RecoveryCoordinator(_pdfium, new RecoveryStore(RecoveryStore.DefaultFolder), _settings,
            new AvaloniaRecoveryUi(this), prompts);
        _recovery.Attach(workflow);
        _vm.PreferencesRequested += async (_, _) => await ShowPreferencesAsync();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // 명령줄 인자로 받은 파일은 창이 뜬 뒤에 연다.
        // 먼저 이전에 남은 복구 항목을 묻고, 그다음 명령줄 파일을 연다(같은 파일이면 복구한 탭으로 전환됨).
        Opened += async (_, _) =>
        {
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

    private void OnRibbonTabClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RibbonTabVm tab) _vm.SelectRibbonTabCommand.Execute(tab.Id);
    }

    private void OnPanelButtonClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PanelButtonVm p) _vm.SetPanelCommand.Execute(p.Kind);
    }

    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DocumentTabViewModel d) _vm.CloseDocumentCommand.Execute(d);
        e.Handled = true;
    }
}
