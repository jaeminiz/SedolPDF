using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PdfEditor.App.Resources;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Panels;

/// <summary>쪽 객체 속성 패널(M4): 위치·크기(mm), 새 도형의 선 색·두께·채우기, 새 텍스트의 크기·색. 값 판단은 ObjectPropertiesViewModel.</summary>
public sealed partial class ObjectPropertiesPanel : UserControl
{
    private ObjectPropertiesViewModel? _vm;
    private bool _syncing;

    private readonly NumericUpDown _x = Box(), _y = Box(), _w = Box(0.1m), _h = Box(0.1m);
    private readonly NumericUpDown _strokeWidth = Box(0.25m, 50, 0.25m, 2);
    private readonly NumericUpDown _textSize = Box(4, 200, 1, 1);
    private readonly WrapPanel _stroke = new(), _fill = new(), _textColor = new();
    private readonly StackPanel _shapeSection = new(), _textSection = new();

    public ObjectPropertiesPanel()
    {
        InitializeComponent();
        Root.Children.Add(new TextBlock { Text = Strings.Prop_Obj_Title, FontWeight = FontWeight.SemiBold });
        Root.Children.Add(Row(Strings.Prop_Obj_X, _x));
        Root.Children.Add(Row(Strings.Prop_Obj_Y, _y));
        Root.Children.Add(Row(Strings.Prop_Obj_W, _w));
        Root.Children.Add(Row(Strings.Prop_Obj_H, _h));
        foreach (var n in new[] { _x, _y, _w, _h }) n.ValueChanged += (_, _) => { if (!_syncing && _vm is not null) _ = CommitBoundsAsync(); };

        _shapeSection.Children.Add(Label(Strings.Prop_Obj_Stroke)); _shapeSection.Children.Add(_stroke);
        _shapeSection.Children.Add(Row(Strings.Prop_Obj_StrokeWidth, _strokeWidth));
        _shapeSection.Children.Add(Label(Strings.Prop_Obj_Fill)); _shapeSection.Children.Add(_fill);
        _strokeWidth.ValueChanged += (_, e) => { if (!_syncing && _vm is not null && e.NewValue is { } v) _ = _vm.SetShapeStyleAsync(_vm.Stroke, (float)v, _vm.Fill); };
        Root.Children.Add(_shapeSection);

        _textSection.Children.Add(Row(Strings.Prop_Obj_TextSize, _textSize));
        _textSection.Children.Add(Label(Strings.Prop_Annot_Color)); _textSection.Children.Add(_textColor);
        _textSize.ValueChanged += (_, e) => { if (!_syncing && _vm is not null && e.NewValue is { } v) _ = _vm.SetTextStyleAsync((float)v, null); };
        Root.Children.Add(_textSection);

        DataContextChanged += (_, _) => Attach();
    }

    private static NumericUpDown Box(decimal min = -100000, decimal max = 100000, decimal inc = 0.5m, int digits = 1) => new()
    {
        Minimum = min, Maximum = max, Increment = inc, FormatString = "0." + new string('0', digits), HorizontalAlignment = HorizontalAlignment.Stretch,
        ParsingNumberStyle = System.Globalization.NumberStyles.Float,
    };

    private static TextBlock Label(string text) => new() { Text = text, Foreground = new SolidColorBrush(Color.Parse("#5C5C5C")), FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };

    private static StackPanel Row(string label, Control box)
    {
        var p = new StackPanel();
        p.Children.Add(Label(label));
        p.Children.Add(box);
        return p;
    }

    private void Attach()
    {
        if (_vm is not null) _vm.Updated -= Sync;
        _vm = DataContext as ObjectPropertiesViewModel;
        if (_vm is not null) _vm.Updated += Sync;
        Sync();
    }

    private async Task CommitBoundsAsync()
    {
        if (_vm is null) return;
        await _vm.SetBoundsAsync(_x.Value is { } x ? (double)x : 0, _y.Value is { } y ? (double)y : 0, _w.Value is { } w ? (double)w : 0, _h.Value is { } h ? (double)h : 0);
    }

    private void Sync()
    {
        if (_vm is null || !_vm.HasSelection) return;
        _syncing = true;
        try
        {
            _x.Value = (decimal)Math.Round(_vm.XMm, 1); _y.Value = (decimal)Math.Round(_vm.YMm, 1);
            _w.Value = (decimal)Math.Round(_vm.WidthMm, 1); _h.Value = (decimal)Math.Round(_vm.HeightMm, 1);
            _shapeSection.IsVisible = _vm.IsShape;
            _textSection.IsVisible = _vm.IsAppText;
            if (_vm.IsShape)
            {
                _strokeWidth.Value = (decimal)_vm.StrokeWidth;
                Rebuild(_stroke, _vm.Stroke, includeNone: true, c => _ = _vm.SetShapeStyleAsync(c, _vm.StrokeWidth, _vm.Fill));
                Rebuild(_fill, _vm.Fill, includeNone: true, c => _ = _vm.SetShapeStyleAsync(_vm.Stroke, _vm.StrokeWidth, c));
            }
            if (_vm.IsAppText)
            {
                _textSize.Value = (decimal)_vm.TextSizePt;
                Rebuild(_textColor, _vm.TextColor, includeNone: false, c => _ = _vm.SetTextStyleAsync(null, c));
            }
        }
        finally { _syncing = false; }
    }

    private void Rebuild(WrapPanel host, PdfColor? current, bool includeNone, Action<PdfColor?> onPick)
    {
        host.Children.Clear();
        if (includeNone) host.Children.Add(PropertiesPanel.MakeSwatch(null, current is null, onPick));
        foreach (var c in _vm!.Swatches) host.Children.Add(PropertiesPanel.MakeSwatch(c, current == c, onPick));
    }
}
