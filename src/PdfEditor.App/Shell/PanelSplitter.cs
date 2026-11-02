using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PdfEditor.App.Shell;

/// <summary>패널 가장자리의 얇은 크기 조절 손잡이: 가로로 끌면 DragDelta(DIP), 더블클릭하면 Reset.</summary>
public sealed class PanelSplitter : Control
{
    private Point? _start;

    public event EventHandler<double>? DragDelta;
    public event EventHandler? Reset;

    public PanelSplitter()
    {
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        Width = 8;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));   // 빈 곳도 눌림 판정이 되게
        if (IsPointerOver || _start is not null)
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x5F, 0xB8)), new Rect(Bounds.Width / 2 - 1, 4, 2, Math.Max(0, Bounds.Height - 8)));
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { Reset?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
        _start = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_start is not { } s) return;
        double dx = e.GetPosition(this).X - s.X;
        if (dx != 0) DragDelta?.Invoke(this, dx);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_start is null) return;
        _start = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }
}
