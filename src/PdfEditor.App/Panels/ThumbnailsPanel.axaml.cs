using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace PdfEditor.App.Panels;

/// <summary>썸네일 패널. 선택·보이는 칸 알림·Delete 키·현재 쪽 스크롤을 모델에 이어 준다. (화면 동작은 직접 확인한 것만 보고서에 적는다)</summary>
public sealed partial class ThumbnailsPanel : UserControl
{
    private ThumbnailsViewModel? _vm;

    public ThumbnailsPanel()
    {
        InitializeComponent();
        List.ContainerPrepared += (_, e) =>
        {
            if (e.Container.DataContext is ThumbnailItem it) _vm?.MarkVisible(it.Id);
        };
        List.ContainerClearing += (_, e) =>
        {
            if (e.Container.DataContext is ThumbnailItem it) _vm?.MarkHidden(it.Id);
        };
        List.SelectionChanged += (_, e) =>
        {
            _vm?.SetSelection(List.SelectedItems?.OfType<ThumbnailItem>().ToList() ?? [], e.AddedItems.OfType<ThumbnailItem>().LastOrDefault());
        };
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && _vm?.Commands is { } c && c.DeleteCommand.CanExecute(null))
            {
                _ = c.DeleteCommand.ExecuteAsync(null);
                e.Handled = true;
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.CurrentPageChanged -= OnCurrentPageChanged;
        _vm = DataContext as ThumbnailsViewModel;
        if (_vm is not null) _vm.CurrentPageChanged += OnCurrentPageChanged;
    }

    private void OnCurrentPageChanged(int index)
    {
        if (index >= 0 && index < (_vm?.Items.Count ?? 0)) List.ScrollIntoView(index);
    }
}
