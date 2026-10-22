using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PdfEditor.App.Resources;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Dialogs;

/// <summary>
/// 사용자 색 고르기(2026-10-22 사용자 요청: 견본 10색 말고 원하는 색): 채도·명도 사각형 + 색상 띠 + RGB 값 + 16진수 입력.
/// 확인하면 Result에 고른 색, 취소하면 null. 직접 만든 컨트롤이라 외부 부품이 필요 없다.
/// </summary>
public sealed class ColorPickerDialog : Window
{
    private const double SvW = 240, SvH = 160, HueH = 18;

    private double _h, _s, _v;
    private bool _syncing;
    private readonly Border _sv = new() { Width = SvW, Height = SvH, ClipToBounds = true };
    private readonly Border _svMarker = new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), BorderBrush = Brushes.White, BorderThickness = new Thickness(2), IsHitTestVisible = false };
    private readonly Border _hue = new() { Width = SvW, Height = HueH, CornerRadius = new CornerRadius(3) };
    private readonly Border _hueMarker = new() { Width = 4, Height = HueH + 6, Background = Brushes.White, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), IsHitTestVisible = false };
    private readonly Border _newSwatch = new() { Width = 56, Height = 34, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private readonly Border _oldSwatch = new() { Width = 56, Height = 34, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private readonly TextBox _hex = new() { Width = 96, MaxLength = 7 };
    private readonly NumericUpDown _r = Channel(), _g = Channel(), _b = Channel();

    public PdfColor? Result { get; private set; }

    public ColorPickerDialog(PdfColor initial)
    {
        Title = Strings.Dlg_ColorTitle;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#F3F3F3"));
        (_h, _s, _v) = ColorMath.ToHsv(initial);
        _oldSwatch.Background = new SolidColorBrush(Color.FromRgb(initial.R, initial.G, initial.B));

        _hue.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = [.. Enumerable.Range(0, 7).Select(i => new GradientStop(Color.FromRgb(ColorMath.FromHsv(i * 60, 1, 1).R, ColorMath.FromHsv(i * 60, 1, 1).G, ColorMath.FromHsv(i * 60, 1, 1).B), i / 6.0))],
        };

        var svHost = new Panel { Width = SvW, Height = SvH };
        svHost.Children.Add(_sv);
        svHost.Children.Add(_svMarker);
        _svMarker.HorizontalAlignment = HorizontalAlignment.Left; _svMarker.VerticalAlignment = VerticalAlignment.Top;
        var hueHost = new Panel { Width = SvW, Height = HueH + 6 };
        hueHost.Children.Add(_hue);
        hueHost.Children.Add(_hueMarker);
        _hueMarker.HorizontalAlignment = HorizontalAlignment.Left; _hueMarker.VerticalAlignment = VerticalAlignment.Center;

        Wire(svHost, (x, y) => { _s = Math.Clamp(x / SvW, 0, 1); _v = 1 - Math.Clamp(y / SvH, 0, 1); Refresh(); });
        Wire(hueHost, (x, _) => { _h = Math.Clamp(x / SvW, 0, 1) * 360; Refresh(); });

        _hex.TextChanged += (_, _) =>
        {
            if (_syncing || ColorMath.TryParseHex(_hex.Text) is not { } c) return;
            (_h, _s, _v) = ColorMath.ToHsv(c);
            Refresh(fromHex: true);
        };
        foreach (var n in new[] { _r, _g, _b })
            n.ValueChanged += (_, _) =>
            {
                if (_syncing) return;
                (_h, _s, _v) = ColorMath.ToHsv(new PdfColor((byte)(_r.Value ?? 0), (byte)(_g.Value ?? 0), (byte)(_b.Value ?? 0)));
                Refresh(fromChannels: true);
            };

        var ok = new Button { Content = Strings.Dlg_Ok, MinWidth = 80, IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = Strings.Dlg_Cancel, MinWidth = 80, IsCancel = true };
        ok.Click += (_, _) => { Result = Current(); Close(Result); };
        cancel.Click += (_, _) => Close(null);

        var values = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        values.Children.Add(Labeled("R", _r));
        values.Children.Add(Labeled("G", _g));
        values.Children.Add(Labeled("B", _b));
        values.Children.Add(Labeled(Strings.Dlg_ColorHex, _hex));
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        swatches.Children.Add(Labeled(Strings.Dlg_ColorOld, _oldSwatch));
        swatches.Children.Add(Labeled(Strings.Dlg_ColorNew, _newSwatch));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(18, 16), Spacing = 10 };
        root.Children.Add(svHost);
        root.Children.Add(hueHost);
        root.Children.Add(values);
        root.Children.Add(swatches);
        root.Children.Add(buttons);
        Content = root;
        Refresh();
    }

    /// <summary>지금 고른 색.</summary>
    public PdfColor Current() => ColorMath.FromHsv(_h, _s, _v);

    private static NumericUpDown Channel() => new() { Minimum = 0, Maximum = 255, Increment = 1, FormatString = "0", Width = 54, ShowButtonSpinner = false };

    private static StackPanel Labeled(string label, Control c)
    {
        var p = new StackPanel { Spacing = 2 };
        p.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#5C5C5C")) });
        p.Children.Add(c);
        return p;
    }

    private static void Wire(Control host, Action<double, double> set)
    {
        bool down = false;
        host.PointerPressed += (_, e) => { down = true; e.Pointer.Capture(host); var p = e.GetPosition(host); set(p.X, p.Y); };
        host.PointerMoved += (_, e) => { if (down) { var p = e.GetPosition(host); set(p.X, p.Y); } };
        host.PointerReleased += (_, e) => { down = false; e.Pointer.Capture(null); };
    }

    private void Refresh(bool fromHex = false, bool fromChannels = false)
    {
        _syncing = true;
        try
        {
            var c = Current();
            var pure = ColorMath.FromHsv(_h, 1, 1);
            _sv.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = [new GradientStop(Colors.White, 0), new GradientStop(Color.FromRgb(pure.R, pure.G, pure.B), 1)],
            };
            _sv.Child = new Border
            {
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = [new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Black, 1)],
                },
            };
            _svMarker.Margin = new Thickness(_s * SvW - 6, (1 - _v) * SvH - 6, 0, 0);
            _svMarker.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            _hueMarker.Margin = new Thickness(_h / 360 * SvW - 2, 0, 0, 0);
            _newSwatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            if (!fromHex) _hex.Text = c.ToHex();
            if (!fromChannels) { _r.Value = c.R; _g.Value = c.G; _b.Value = c.B; }
        }
        finally { _syncing = false; }
    }
}
