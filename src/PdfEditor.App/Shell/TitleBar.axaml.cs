using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace PdfEditor.App.Shell;

public sealed partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
        QuickBox.KeyDown += OnQuickKeyDown;
        QuickList.DoubleTapped += (_, _) => RunSelected();
        QuickList.PointerReleased += (_, _) => RunSelected();
        // 제목 줄의 빈 곳이나 파일 이름 글자를 더블클릭하면 최대화/복원(2026-10-20 사용자 요청). 단추·입력칸·목록 위는 제외.
        DoubleTapped += OnTitleDoubleTapped;
    }

    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Avalonia.Visual v && v.GetSelfAndVisualAncestors().Any(a => a is Button or TextBox or ListBox or ListBoxItem or Popup)) return;
        if (TopLevel.GetTopLevel(this) is not Window w || !w.CanMaximize) return;
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        e.Handled = true;
    }

    /// <summary>Ctrl+. : 빠른 실행 입력칸으로 포커스.</summary>
    public void FocusQuickBox()
    {
        QuickBox.Focus();
        QuickBox.SelectAll();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnQuickKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                RunFirstEnabled();
                e.Handled = true;
                break;
            case Key.Escape:
                Vm.QuickCommandText = "";
                e.Handled = true;
                break;
            case Key.Down when QuickList.ItemCount > 0:
                QuickList.SelectedIndex = Math.Min(QuickList.SelectedIndex + 1, QuickList.ItemCount - 1);
                e.Handled = true;
                break;
            case Key.Up when QuickList.ItemCount > 0:
                QuickList.SelectedIndex = Math.Max(QuickList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
        }
    }

    private void RunFirstEnabled()
    {
        var cmd = QuickList.SelectedItem as AppCommand
                  ?? Vm?.QuickCommandResults.FirstOrDefault(c => c.Command.CanExecute(null));
        Run(cmd);
    }

    private void RunSelected() => Run(QuickList.SelectedItem as AppCommand);

    private void Run(AppCommand? cmd)
    {
        if (cmd is null || Vm is null || !cmd.Command.CanExecute(null)) return;
        Vm.QuickCommandText = "";
        cmd.Command.Execute(null);
    }
}
