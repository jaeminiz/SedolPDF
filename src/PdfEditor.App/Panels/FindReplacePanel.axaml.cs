using Avalonia.Controls;
using Avalonia.Input;
using PdfEditor.App.Viewer;

namespace PdfEditor.App.Panels;

/// <summary>
/// 찾아 바꾸기 패널. Enter = 찾기(미리보기), Esc = 패널 닫기. 한글 IME가 조합 중이거나 방금 끝냈으면 Enter/Esc는 조합을 끝내는 키라 건드리지 않는다(ImeCompositionGuard).
/// </summary>
public sealed partial class FindReplacePanel : UserControl
{
    public FindReplacePanel()
    {
        InitializeComponent();
        foreach (var box in new[] { FindBox, WithBox, RangeBox })
            box.KeyDown += OnBoxKeyDown;
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || DataContext is not FindReplaceViewModel vm) return;
        if (ImeCompositionGuard.For(box).Consume(e)) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (vm.FindCommand.CanExecute(null)) _ = vm.FindCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.RequestClose();
        }
    }

    public void FocusFind()
    {
        FindBox.Focus();
        FindBox.SelectAll();
    }
}
