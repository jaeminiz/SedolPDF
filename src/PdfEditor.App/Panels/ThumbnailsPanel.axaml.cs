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
        // 썸네일을 끌어 쪽 순서 바꾸기(2026-10-21 사용자 요청). 파일 끌어 놓기와 달리 포인터 이벤트로 처리한다.
        List.AddHandler(PointerPressedEvent, OnThumbPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        List.AddHandler(PointerMovedEvent, OnThumbMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        List.AddHandler(PointerReleasedEvent, OnThumbReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        List.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _dragging)
            {
                EndDrag();
                e.Handled = true;
            }
            if (e.Key == Key.Delete && _vm?.Commands is { } c && c.DeleteCommand.CanExecute(null))
            {
                _ = c.DeleteCommand.ExecuteAsync(null);
                e.Handled = true;
            }
        };
    }


    // ---- 쪽 끌어 옮기기 ----

    private const double DragThreshold = 6;
    private ThumbnailItem? _pressed;
    private Point _pressPoint;
    private bool _collapseOnRelease;
    private bool _dragging;
    private int _dropIndex = -1;

    private void OnThumbPressed(object? sender, PointerPressedEventArgs e)
    {
        EndDrag();
        if (!e.GetCurrentPoint(List).Properties.IsLeftButtonPressed || _vm?.Commands is null) return;
        var item = (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is not ThumbnailItem it) return;
        _pressed = it;
        _pressPoint = e.GetPosition(List);
        // 여러 쪽을 고른 채 그중 하나를 눌러 끌 수 있도록, 수정키 없이 고른 쪽을 누르면 선택 정리는 손을 뗄 때(끌지 않았을 때) 한다.
        if (e.KeyModifiers == KeyModifiers.None && item.IsSelected && (List.SelectedItems?.Count ?? 0) > 1)
        {
            _collapseOnRelease = true;
            e.Handled = true;
        }
    }

    private void OnThumbMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null) return;
        var pos = e.GetPosition(List);
        if (!_dragging)
        {
            if (!e.GetCurrentPoint(List).Properties.IsLeftButtonPressed) { EndDrag(); return; }
            if (Math.Abs(pos.Y - _pressPoint.Y) < DragThreshold && Math.Abs(pos.X - _pressPoint.X) < DragThreshold) return;
            _dragging = true;
            e.Pointer.Capture(List);
        }
        e.Handled = true;
        AutoScroll(pos.Y);
        UpdateDropLine(pos);
    }

    private void OnThumbReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            var dragged = _pressed;
            int drop = _dropIndex;
            EndDrag();
            e.Handled = true;
            if (dragged is not null && drop >= 0 && _vm?.Commands is { } c) _ = c.MovePagesToDropAsync(dragged.Id, drop);
        }
        else if (_collapseOnRelease && _pressed is { } it)
        {
            _collapseOnRelease = false;
            List.SelectedItem = it;
            e.Handled = true;
        }
        _pressed = null;
        _collapseOnRelease = false;
    }

    private void EndDrag()
    {
        _dragging = false;
        _dropIndex = -1;
        _pressed = null;
        _collapseOnRelease = false;
        DropLine.IsVisible = false;
    }

    private void AutoScroll(double y)
    {
        var sv = List.FindDescendantOfType<ScrollViewer>();
        if (sv is null) return;
        const double edge = 28, step = 24;
        if (y < edge) sv.Offset = sv.Offset.WithY(Math.Max(0, sv.Offset.Y - step));
        else if (y > List.Bounds.Height - edge) sv.Offset = sv.Offset.WithY(Math.Min(sv.Extent.Height - sv.Viewport.Height, sv.Offset.Y + step));
    }

    /// <summary>칸을 배치하는 패널(여러 열 배치).</summary>
    private ThumbnailGridPanel? GridPanel => List.ItemsPanelRoot as ThumbnailGridPanel;

    /// <summary>목록 좌표의 점을 패널 좌표(스크롤된 전체 내용 기준)로 바꿔 끌어 놓을 자리를 구한다.</summary>
    private ThumbnailGridLayout.DropSlot? SlotAt(Point listPoint)
    {
        if (GridPanel is not { } panel || _vm is null || List.TranslatePoint(listPoint, panel) is not { } p) return null;
        return ThumbnailGridLayout.DropSlotAt(p, _vm.Items.Count, panel.Columns, panel.ContentWidth, panel.CellWidth, panel.RowTops);
    }

    private void UpdateDropLine(Point pos)
    {
        if (SlotAt(pos) is not { } slot || GridPanel is not { } panel) return;
        _dropIndex = slot.Index;
        var topLeft = panel.TranslatePoint(slot.Caret.TopLeft, DropHost) ?? default;
        DropLine.Width = slot.Caret.Width;
        DropLine.Height = slot.Caret.Height;
        DropLine.Margin = new Thickness(Math.Max(0, topLeft.X), Math.Max(0, topLeft.Y), 0, 0);
        DropLine.IsVisible = true;
    }

    private static IEnumerable<string> PdfPaths(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath())
            .Where(p => p is not null && p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).Select(p => p!);

    /// <summary>놓은 자리: 썸네일 칸의 위쪽 절반이면 그 쪽 앞, 아래쪽 절반이면 그 쪽 뒤. 칸 밖이면 맨 끝.</summary>
    private int DropIndex(DragEventArgs e)
    {
        return SlotAt(e.GetPosition(List))?.Index ?? _vm?.Items.Count ?? 0;
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
