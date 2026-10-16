using Avalonia.Controls;
using Avalonia.Input;

namespace PdfEditor.App.Panels;

/// <summary>책갈피 패널. 항목을 누르면 그 쪽으로 이동한다(목적지가 없는 항목은 아무 일도 없다).</summary>
public sealed partial class BookmarksPanel : UserControl
{
    public BookmarksPanel()
    {
        InitializeComponent();
        Tree.AddHandler(TappedEvent, (_, e) =>
        {
            if (e.Source is Control { DataContext: BookmarkNode node } && DataContext is BookmarksViewModel vm)
                vm.Navigate(node);
        });
    }
}
