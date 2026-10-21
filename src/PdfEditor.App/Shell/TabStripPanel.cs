using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace PdfEditor.App.Shell;

/// <summary>
/// 문서 탭 줄 배치판. 탭 폭 = 사용 가능한 폭을 탭 수로 나눈 값을 [MinTabWidth, MaxTabWidth]로 맞춘 것(브라우저 탭처럼 균등 축소).
/// 최소 폭에서도 넘치면 고른 탭이 보이도록 줄 전체를 왼쪽으로 민다(넘친 부분은 잘림).
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

    private double _width = MaxTabWidth;

    static TabStripPanel()
    {
        // 고른 탭이 바뀌면 줄을 다시 배치해 고른 탭이 보이게 한다.
        SelectingItemsControl.IsSelectedProperty.Changed.AddClassHandler<ListBoxItem>(
            (item, _) => (item.GetVisualParent() as TabStripPanel)?.InvalidateArrange());
    }

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
        double selRight = 0;
        for (int i = 0; i < n; i++)
            if (Children[i] is ListBoxItem { IsSelected: true }) selRight = i * (_width + Gap) + _width;
        double x = -ScrollOffset(finalSize.Width, total, selRight);
        foreach (var c in Children)
        {
            c.Arrange(new Rect(x, 0, _width, finalSize.Height));
            x += _width + Gap;
        }
        return finalSize;
    }

    /// <summary>탭 i의 줄 안 가로 위치와 폭(끌어 놓기 계산용). 화면에 놓인 값이 아니라 현재 배치된 값.</summary>
    public IReadOnlyList<(double Left, double Width)> TabBounds() => [.. Children.Select(c => (c.Bounds.X, c.Bounds.Width))];
}
