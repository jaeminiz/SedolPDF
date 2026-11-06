// 스파이크(버릴 코드): 0.8.0 단계 0. 제품 코드에 복사하지 말 것.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace S5.MultiWindowDrag;

/// <summary>위쪽에 가짜 탭 줄(높이 32), 아래에 상태 글이 있는 창. 탭을 끌어 다른 창 탭 줄이나 바깥에 놓는다.</summary>
public sealed class SpikeWindow : Window
{
    public static readonly List<SpikeWindow> All = [];   // 최근 활성 창이 앞
    private static int _count;

    public string Label { get; }
    public StackPanel Strip { get; } = new() { Orientation = Orientation.Horizontal, Height = 32, Background = Brushes.LightGray };
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };

    private Border? _pressed;
    private Point _pressAt;
    private bool _dragging;

    public SpikeWindow(params string[] tabs)
    {
        Label = "W" + (++_count);
        Title = "S5 spike " + Label;
        Width = 520; Height = 260;
        foreach (var t in tabs) Strip.Children.Add(MakeTab(t));
        var root = new DockPanel();
        DockPanel.SetDock(Strip, Dock.Top);
        root.Children.Add(Strip);
        root.Children.Add(_status);
        Content = root;
        Strip.AddHandler(PointerPressedEvent, OnPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Strip.AddHandler(PointerMovedEvent, OnMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Strip.AddHandler(PointerReleasedEvent, OnReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Strip.AddHandler(PointerCaptureLostEvent, (_, _) => { if (_dragging) Log.Write($"{Label} capture lost"); End(); },
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || !_dragging) return;
            Log.Write($"{Label} ESC cancel");
            Program.Results.Add("esc-cancel");
            End();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Activated += (_, _) => { All.Remove(this); All.Insert(0, this); };
        Closed += (_, _) => All.Remove(this);
        All.Insert(0, this);
    }

    public static Border MakeTab(string text) => new()
    {
        Width = 90, Margin = new Thickness(1, 2), Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) },
    };

    /// <summary>탭 줄의 화면(물리 픽셀) 사각형.</summary>
    public PixelRect StripScreenRect()
    {
        var a = Strip.PointToScreen(new Point(0, 0));
        var b = Strip.PointToScreen(new Point(Strip.Bounds.Width, Strip.Bounds.Height));
        return new PixelRect(a, b);
    }

    public PixelPoint TabCenterOnScreen(int i)
    {
        var t = (Control)Strip.Children[i];
        return t.PointToScreen(new Point(t.Bounds.Width / 2, t.Bounds.Height / 2));
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Strip).Properties.IsLeftButtonPressed) return;
        _pressed = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Parent == Strip);
        _pressAt = e.GetPosition(Strip);
        Log.Write($"{Label} press tab={TabText(_pressed)} at {_pressAt}");
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null) return;
        var p = e.GetPosition(Strip);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _pressAt.X) < 6 && Math.Abs(p.Y - _pressAt.Y) < 6) return;
            _dragging = true;
            e.Pointer.Capture(Strip);
            Log.Write($"{Label} drag start (captured={e.Pointer.Captured == Strip})");
        }
        var screen = this.PointToScreen(e.GetPosition(this));
        var (target, x) = HitTest(screen);
        string where = target is null ? "none" : $"{target.Label} x={x:F0}dip";
        _status.Text = $"screen={screen} over {where} scaling={RenderScaling}";
        Log.Write($"{Label} move screen={screen} over={where}");
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) { _pressed = null; return; }
        var tab = _pressed!;
        var screen = this.PointToScreen(e.GetPosition(this));
        var (target, _) = HitTest(screen);
        Log.Write($"{Label} release screen={screen} over={target?.Label ?? "none"}");
        End();
        if (target == this) { Program.Results.Add("reorder"); return; }
        // 같은 컨트롤을 다른 창 트리로 바로 옮기면 Avalonia가 "wrong LayoutManager"로 죽는다(1차 실행에서 확인).
        // 제품도 창마다 탭 모델·컨테이너를 새로 만들므로, 여기서도 새 탭을 만든다.
        Strip.Children.Remove(tab);
        tab = MakeTab(TabText(tab));
        if (target is not null)
        {
            target.Strip.Children.Add(tab);
            Program.Results.Add($"moved-to-{target.Label}");
        }
        else
        {
            var w = new SpikeWindow { Position = new PixelPoint(screen.X - 40, screen.Y - 16) };
            w.Strip.Children.Add(tab);
            w.Show();
            Program.Results.Add($"new-window-{w.Label}");
        }
        Log.Write($"{Label} drop done results=[{string.Join(",", Program.Results)}]");
    }

    /// <summary>최근 활성 창부터 탭 줄 사각형(위아래 32 DIP 여유)에 포인터가 드는지 본다. x = 그 탭 줄 안 DIP 위치.</summary>
    private static (SpikeWindow?, double) HitTest(PixelPoint screen)
    {
        foreach (var w in All)
        {
            var r = w.StripScreenRect();
            int pad = (int)(32 * w.RenderScaling);
            var grown = new PixelRect(r.X, r.Y - pad, r.Width, r.Height + 2 * pad);
            if (grown.Contains(screen)) return (w, (screen.X - r.X) / w.RenderScaling);
        }
        return (null, 0);
    }

    private void End()
    {
        _dragging = false;
        _pressed = null;
    }

    private static string TabText(Border? b) => (b?.Child as TextBlock)?.Text ?? "-";
}
