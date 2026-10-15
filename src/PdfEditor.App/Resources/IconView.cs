using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PdfEditor.App.Resources;

/// <summary>Icons.axaml의 StreamGeometry("Icon.&lt;Key&gt;", Fluent UI System Icons)를 20x20 기준으로 채워 그린다. 키가 없으면 Icon.generic.</summary>
public sealed class IconView : Control
{
    public static readonly StyledProperty<string?> KeyProperty = AvaloniaProperty.Register<IconView, string?>(nameof(Key));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<IconView>();

    static IconView()
    {
        AffectsRender<IconView>(KeyProperty, ForegroundProperty);
        AffectsMeasure<IconView>(KeyProperty);
    }

    public string? Key { get => GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public static Geometry? Lookup(string? key)
    {
        var app = Application.Current;
        if (app is null) return null;
        if (key is not null && app.TryGetResource("Icon." + key, app.ActualThemeVariant, out var v) && v is Geometry g) return g;
        return app.TryGetResource("Icon.generic", app.ActualThemeVariant, out var d) ? d as Geometry : null;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsNaN(Width) ? 20 : Width, double.IsNaN(Height) ? 20 : Height);

    public override void Render(DrawingContext context)
    {
        var g = Lookup(Key);
        if (g is null) return;
        double s = Math.Min(Bounds.Width, Bounds.Height) / 20.0;
        using (context.PushTransform(Matrix.CreateScale(s, s)))
            context.DrawGeometry(Foreground ?? Brushes.Black, null, g);
    }
}
