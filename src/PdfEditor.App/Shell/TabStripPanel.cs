using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PdfEditor.App.Shell;

/// <summary>
/// 문서 탭 줄 배치판. 탭 폭 = 사용 가능한 폭을 탭 수로 나눈 값을 [MinTabWidth, MaxTabWidth]로 맞춘 것(브라우저 탭처럼 균등 축소).
/// 최소 폭에서도 넘치면 줄을 좌우로 밀 수 있다(왼쪽·오른쪽 스크롤 단추, 마우스 휠). 고른 탭이 바뀌거나 탭이 늘면 고른 탭이 보이는 만큼만 자동으로 민다.
/// </summary>
public sealed class TabStripPanel : Panel
{
    public const double MaxTabWidth = 240;
    public const double MinTabWidth = 72;
    public const double Gap = 2;

    /// <summary>탭 하나의 폭(순수 계산). 남는 폭이 없거나 탭이 없으면 최대 폭.</summary>
    public static double TabWidth(double available, int count)
    {
        if (count <= 0 || double.IsInfinity(available)) return MaxTabWidth;
        double each = (available - Gap * (count - 1)) / count;
        return Math.Clamp(each, MinTabWidth, MaxTabWidth);
    }

    /// <summary>줄을 민 거리(순수 계산): 고른 탭의 오른쪽 끝이 보이는 폭 안에 들어오도록. 넘치지 않으면 0.</summary>
    public static double ScrollOffset(double available, double total, double selectedRight)
        => total <= available || double.IsInfinity(available) ? 0 : Math.Clamp(selectedRight - available, 0, total - available);

    /// <summary>현재 민 거리 offset에서 [left, right] 탭이 완전히 보이게 하는 가장 가까운 거리(순수 계산). 이미 보이면 그대로.</summary>
    public static double RevealOffset(double offset, double available, double total, double left, double right)
    {
        if (total <= available || double.IsInfinity(available)) return 0;
        double o = offset;
        if (left < o) o = left;
        else if (right > o + available) o = right - available;
        return Math.Clamp(o, 0, total - available);
    }

    /// <summary>줄이 넘치는가·민 거리가 바뀌었음을 알린다(스크롤 단추 표시·사용 가능 갱신용). 배치가 끝난 뒤 UI 스레드에서.</summary>
    public static event Action<TabStripPanel>? ScrollStateChanged;

    private double _width = MaxTabWidth;
    private double _offset;
    private double _maxOffset;
    private int _lastCount = -1;
    private bool _reveal = true;

    /// <summary>지금 줄을 민 거리(0 = 맨 왼쪽).</summary>
    public double Offset => _offset;

    /// <summary>민 수 있는 가장 먼 거리. 0이면 넘치지 않는다.</summary>
    public double MaxOffset => _maxOffset;

    public bool IsOverflowing => _maxOffset > 0.5;
    public bool CanScrollLeft => _offset > 0.5;
    public bool CanScrollRight => _offset < _maxOffset - 0.5;

    static TabStripPanel()
    {
        // 고른 탭이 바뀌면 줄을 다시 배치해 고른 탭이 보이게 한다.
        SelectingItemsControl.IsSelectedProperty.Changed.AddClassHandler<ListBoxItem>((item, _) =>
        {
            if (item.GetVisualParent() is TabStripPanel p) { p._reveal = true; p.InvalidateArrange(); }
        });
    }

    public TabStripPanel()
    {
        PointerWheelChanged += (_, e) =>
        {
            if (!IsOverflowing) return;
            ScrollBy(-(e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X) * 80);
            e.Handled = true;
        };
    }

    /// <summary>줄을 delta만큼 민다(양수 = 오른쪽 탭이 보이는 쪽). 범위를 넘지 않는다.</summary>
    public void ScrollBy(double delta)
    {
        double next = Math.Clamp(_offset + delta, 0, _maxOffset);
        if (Math.Abs(next - _offset) < 0.01) return;
        _offset = next;
        _reveal = false;
        InvalidateArrange();
    }

    /// <summary>한 번 누를 때 미는 거리: 탭 두 개 폭.</summary>
    public double PageStep => 2 * (_width + Gap);

    protected override Size MeasureOverride(Size availableSize)
    {
        _width = TabWidth(availableSize.Width, Children.Count);
        double h = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(_width, availableSize.Height));
            h = Math.Max(h, c.DesiredSize.Height);
        }
        int n = Children.Count;
        double total = n == 0 ? 0 : n * _width + Gap * (n - 1);
        return new Size(double.IsInfinity(availableSize.Width) ? total : Math.Min(total, availableSize.Width), h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int n = Children.Count;
        double total = n == 0 ? 0 : n * _width + Gap * (n - 1);
        double oldMax = _maxOffset, oldOffset = _offset;
        _maxOffset = Math.Max(0, total - finalSize.Width);
        if (n != _lastCount) { _reveal = true; _lastCount = n; }   // 탭이 늘거나 줄면 고른 탭이 보이게
        _offset = Math.Clamp(_offset, 0, _maxOffset);
        if (_reveal)
        {
            for (int i = 0; i < n; i++)
                if (Children[i] is ListBoxItem { IsSelected: true })
                {
                    double left = i * (_width + Gap);
                    _offset = RevealOffset(_offset, finalSize.Width, total, left, left + _width);
                }
            _reveal = false;
        }
        double x = -_offset;
        foreach (var c in Children)
        {
            c.Arrange(new Rect(x, 0, _width, finalSize.Height));
            x += _width + Gap;
        }
        if (Math.Abs(oldMax - _maxOffset) > 0.5 || Math.Abs(oldOffset - _offset) > 0.5)
            Dispatcher.UIThread.Post(() => ScrollStateChanged?.Invoke(this), DispatcherPriority.Loaded);
        return finalSize;
    }

    /// <summary>탭 i의 줄 안 가로 위치와 폭(끌어 놓기 계산용). 화면에 놓인 값이 아니라 현재 배치된 값.</summary>
    public IReadOnlyList<(double Left, double Width)> TabBounds() => [.. Children.Select(c => (c.Bounds.X, c.Bounds.Width))];
}
