using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PdfEditor.App.Shell;

public sealed partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
        QuickBox.KeyDown += OnQuickKeyDown;
        QuickList.DoubleTapped += (_, _) => RunSelected();
        QuickList.PointerReleased += (_, _) => RunSelected();
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
