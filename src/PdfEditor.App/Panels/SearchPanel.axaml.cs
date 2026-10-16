using Avalonia.Controls;
using Avalonia.Input;

namespace PdfEditor.App.Panels;

/// <summary>검색 패널. Enter = 검색. 찾기 명령이 부르면 입력칸에 초점을 준다.</summary>
public sealed partial class SearchPanel : UserControl
{
    public SearchPanel()
    {
        InitializeComponent();
        QueryBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is SearchViewModel vm && vm.SearchCommand.CanExecute(null))
            {
                _ = vm.SearchCommand.ExecuteAsync(null);
                e.Handled = true;
            }
        };
    }

    public void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }
}
