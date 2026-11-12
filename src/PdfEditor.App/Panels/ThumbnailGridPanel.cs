using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace PdfEditor.App.Panels;

/// <summary>
/// 썸네일 여러 열 배치 패널(0.11.0). 같은 크기의 칸을 폭에 맞는 열 수로 채우고, 보이는 줄(위아래 한 줄 여유)의 칸만 만든다.
/// 칸 크기가 모두 같아 위치는 번호만으로 계산된다(ThumbnailGridLayout). 전체 높이는 줄 수 × 칸 높이라서
/// 300쪽 넘는 문서도 스크롤 길이가 맞고 실제 칸은 수십 개만 있다. 보이는 영역은 EffectiveViewportChanged로 안다.
/// </summary>
public sealed class ThumbnailGridPanel : VirtualizingPanel
{
    public static readonly StyledProperty<double> CellWidthProperty = AvaloniaProperty.Register<ThumbnailGridPanel, double>(nameof(CellWidth), 180);
    public static readonly StyledProperty<double> CellHeightProperty = AvaloniaProperty.Register<ThumbnailGridPanel, double>(nameof(CellHeight), 200);

    /// <summary>줄 높이 = 그 줄에서 가장 큰 썸네일 높이(ThumbnailItem.DisplayHeight) + RowPadding. ThumbnailItem이 아닌 칸은 CellHeight를 쓴다.</summary>
    public static readonly StyledProperty<double> RowPaddingProperty = AvaloniaProperty.Register<ThumbnailGridPanel, double>(nameof(RowPadding), 0);
    /// <summary>칸 크기가 바뀌어도 숫자가 같을 수 있는 경우(쪽 회전 등)에 모델이 올려 주면 다시 배치한다.</summary>
    public static readonly StyledProperty<int> LayoutVersionProperty = AvaloniaProperty.Register<ThumbnailGridPanel, int>(nameof(LayoutVersion));

    static ThumbnailGridPanel() => AffectsMeasure<ThumbnailGridPanel>(CellWidthProperty, CellHeightProperty, RowPaddingProperty, LayoutVersionProperty);

    public double CellWidth { get => GetValue(CellWidthProperty); set => SetValue(CellWidthProperty, value); }
    public double CellHeight { get => GetValue(CellHeightProperty); set => SetValue(CellHeightProperty, value); }
    public double RowPadding { get => GetValue(RowPaddingProperty); set => SetValue(RowPaddingProperty, value); }
    public int LayoutVersion { get => GetValue(LayoutVersionProperty); set => SetValue(LayoutVersionProperty, value); }

    private readonly Dictionary<int, Control> _realized = [];
    private Rect _viewport;
    private double _width = 1;
    private int _columns = 1;
    private double[] _rowTops = [0];
    private int _pinned = -1;   // 키로 옮겨 가는 중인 칸: 스크롤이 끝나 보이는 범위에 들어올 때까지 거두지 않는다

    public ThumbnailGridPanel() => EffectiveViewportChanged += OnEffectiveViewportChanged;

    /// <summary>지금 열 수(마지막으로 잰 폭 기준).</summary>
    public int Columns => _columns;
    /// <summary>줄 위쪽 y 목록(길이 = 줄 수 + 1). 마지막으로 잰 기준.</summary>
    public double[] RowTops => _rowTops;
    /// <summary>마지막으로 잰 폭.</summary>
    public double ContentWidth => _width;

    private (int First, int Last) Range(Rect viewport, int columns, int count)
    {
        double top = viewport.Top, bottom = viewport.Bottom;
        if (viewport.Height <= 0) { top = 0; bottom = 600; }   // 첫 측정: 보이는 영역을 아직 모를 때 한 화면쯤
        return ThumbnailGridLayout.VisibleRange(top, bottom, _rowTops, columns, count);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var old = _viewport;
        _viewport = e.EffectiveViewport;
        int count = Items.Count;
        if (Range(old, _columns, count) != Range(_viewport, _columns, count)) InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Items.Count;
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : CellWidth;
        _width = width;
        _columns = ThumbnailGridLayout.Columns(width, CellWidth);
        _rowTops = BuildRowTops(count, _columns);
        var (first, last) = Range(_viewport, _columns, count);

        // 포커스가 있는 칸과 키로 옮겨 가는 중인 칸은 범위 밖이어도 거두지 않는다(거두면 포커스를 잃어 PageUp/PageDown이 한 번만 먹는다).
        if (_pinned >= first && _pinned <= last) _pinned = -1;
        foreach (var (i, c) in _realized.ToList())
            if ((i < first || i > last) && i != _pinned && !c.IsKeyboardFocusWithin) Release(i);
        for (int i = first; i <= last; i++)
            if (!_realized.ContainsKey(i)) Realize(i);
        foreach (var (i, c) in _realized) c.Measure(new Size(CellWidth, RowHeight(i / _columns)));

        return new Size(width, _rowTops[^1]);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _width = finalSize.Width;
        foreach (var (i, c) in _realized)
            c.Arrange(ThumbnailGridLayout.CellRect(i, _columns, finalSize.Width, CellWidth, _rowTops));
        return finalSize;
    }

    private double ItemHeight(object? item) => item is ThumbnailItem t ? t.DisplayHeight + RowPadding : CellHeight;

    private double RowHeight(int row) => _rowTops[row + 1] - _rowTops[row];

    private double[] BuildRowTops(int count, int columns)
    {
        var heights = new double[ThumbnailGridLayout.Rows(count, columns)];
        var items = Items;
        for (int i = 0; i < count; i++) heights[i / columns] = Math.Max(heights[i / columns], ItemHeight(items[i]));
        return ThumbnailGridLayout.RowTops(heights);
    }

    private Control Realize(int index)
    {
        var item = Items[index];
        var gen = ItemContainerGenerator!;
        var c = gen.NeedsContainer(item, index, out var recycleKey) ? gen.CreateContainer(item, index, recycleKey) : (Control)item!;
        gen.PrepareItemContainer(c, item, index);
        AddInternalChild(c);
        _realized[index] = c;
        gen.ItemContainerPrepared(c, item, index);
        return c;
    }

    private void Release(int index)
    {
        if (!_realized.Remove(index, out var c)) return;
        ItemContainerGenerator!.ClearItemContainer(c);
        RemoveInternalChild(c);
    }

    private void ReleaseAll()
    {
        foreach (int i in _realized.Keys.ToList()) Release(i);
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // 칸 번호가 밀리거나 바뀌므로 만들어 둔 칸을 모두 거두고 다시 만든다(칸 객체·이미지는 모델이 그대로 갖고 있다).
        ReleaseAll();
        InvalidateMeasure();
    }

    protected override Control? ContainerFromIndex(int index) => _realized.GetValueOrDefault(index);

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (i, c) in _realized)
            if (ReferenceEquals(c, container)) return i;
        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers() => _realized.Values;

    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count) return null;
        var c = _realized.GetValueOrDefault(index) ?? Realize(index);
        _pinned = index;
        var rect = ThumbnailGridLayout.CellRect(index, _columns, _width, CellWidth, _rowTops);
        if (!c.IsMeasureValid) c.Measure(rect.Size);
        c.Arrange(rect);
        c.BringIntoView();
        return c;
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        int count = Items.Count;
        if (count == 0) return null;
        int cur = from is Control fc ? IndexFromContainer(fc) : -1;
        double avgRow = Math.Max(1, _rowTops[^1] / Math.Max(1, _rowTops.Length - 1));
        int pageRows = Math.Max(1, (int)(Math.Max(_viewport.Height, avgRow) / avgRow) - 1);
        int target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Left or NavigationDirection.Previous => cur - 1,
            NavigationDirection.Right or NavigationDirection.Next => cur + 1,
            NavigationDirection.Up => cur - _columns,
            NavigationDirection.Down => cur + _columns,
            NavigationDirection.PageUp => cur - _columns * pageRows,
            NavigationDirection.PageDown => cur + _columns * pageRows,
            _ => cur,
        };
        if (cur < 0) target = 0;
        // 위·아래 끝에서 끝 칸을 넘어가면 그 방향의 끝 칸에 머문다(맨 아래 줄이 덜 찼을 때 마지막 칸으로).
        if (target < 0) target = direction is NavigationDirection.Up or NavigationDirection.PageUp ? cur : (wrap ? count - 1 : cur);
        else if (target >= count) target = direction is NavigationDirection.Down or NavigationDirection.PageDown && cur / _columns < (count - 1) / _columns ? count - 1 : (wrap ? 0 : cur);
        return target < 0 || target >= count ? null : ScrollIntoView(target);
    }
}
