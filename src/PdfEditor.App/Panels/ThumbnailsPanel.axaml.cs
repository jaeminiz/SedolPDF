using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

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
        // 썸네일 위에 PDF 파일을 끌어다 놓으면 그 자리에 쪽을 추가한다(2026-10-20 사용자 요청). 창 전체의 "파일 열기" 끌어다 놓기보다 먼저 처리한다.
        List.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (!PdfPaths(e).Any() || _vm?.Commands is null) return;
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        });
        List.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = PdfPaths(e).ToList();
            if (paths.Count == 0 || _vm?.Commands is not { } c) return;
            e.Handled = true;
            _ = c.InsertFilesAtAsync(paths, DropIndex(e));
        });
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && _vm?.Commands is { } c && c.DeleteCommand.CanExecute(null))
            {
                _ = c.DeleteCommand.ExecuteAsync(null);
                e.Handled = true;
            }
        };
    }


    private static IEnumerable<string> PdfPaths(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath())
            .Where(p => p is not null && p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).Select(p => p!);

    /// <summary>놓은 자리: 썸네일 칸의 위쪽 절반이면 그 쪽 앞, 아래쪽 절반이면 그 쪽 뒤. 칸 밖이면 맨 끝.</summary>
    private int DropIndex(DragEventArgs e)
    {
        int count = _vm?.Items.Count ?? 0;
        var item = (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is not ThumbnailItem it) return count;
        bool lower = e.GetPosition(item).Y > item.Bounds.Height / 2;
        return ThumbnailsViewModel.DropIndex(it.PageNumber - 1, lower, count);
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
