using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Shell;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        BuildKeyBindings();
        FileTabButton.Flyout = BuildFileMenu();

        _vm.ExitRequested += (_, _) => Close();
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
        var recent = new MenuItem { Header = Strings.Cmd_RecentDocs };
        recent.Items.Add(new MenuItem { Header = Strings.Cmd_NoRecent, IsEnabled = false }); // Task 11에서 최근 문서 목록으로 교체
        flyout.Items.Add(recent);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("file.save"));
        flyout.Items.Add(Item("file.saveAs"));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("file.print"));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("file.prefs"));
        flyout.Items.Add(Item("file.about"));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("file.exit"));
        return flyout;
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
