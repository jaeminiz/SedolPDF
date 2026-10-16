using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Panels;

/// <summary>속성 패널: 문서 속성(읽기 전용) 또는 주석 속성(견본·슬라이더·작성자). 값 판단은 모두 AnnotationPropertiesViewModel에 있다.</summary>
public sealed partial class PropertiesPanel : UserControl
{
    private AnnotationPropertiesViewModel? _vm;
    private bool _syncing;

    public PropertiesPanel()
    {
        InitializeComponent();
        foreach (var s in new[] { WidthSlider, OpacitySlider })
        {
            s.AddHandler(PointerPressedEvent, OnSliderPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            s.AddHandler(PointerReleasedEvent, OnSliderReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            s.AddHandler(PointerCaptureLostEvent, OnSliderReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        WidthSlider.ValueChanged += (_, e) => { if (!_syncing && _vm is not null) _vm.Width = (float)e.NewValue; };
        OpacitySlider.ValueChanged += (_, e) => { if (!_syncing && _vm is not null) _vm.OpacityPercent = (int)Math.Round(e.NewValue); };
        AuthorBox.LostFocus += (_, _) => _ = CommitAuthorAsync();
        AuthorBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = CommitAuthorAsync(); e.Handled = true; } };
        DataContextChanged += (_, _) => Attach();
    }

    private async Task CommitAuthorAsync()
    {
        if (_vm is null) return;
        try { await _vm.CommitAuthorAsync(); } catch (Exception) { }
    }

    private void OnSliderPressed(object? sender, PointerPressedEventArgs e) => _vm?.BeginPreview();

    private async void OnSliderReleased(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        try { await _vm.CommitPreviewAsync(); } catch (Exception) { }
    }

    private void Attach()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = (DataContext as PropertiesViewModel)?.Annotation;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        Sync();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        if (_vm is null) return;
        _syncing = true;
        try
        {
            bool tool = _vm.Mode == AnnotationPropertiesMode.ToolDefaults;
            DefaultsHint.IsVisible = tool;
            MixedHint.IsVisible = _vm.IsColorMixed;
            FillSection.IsVisible = _vm.ShowFill;
            WidthSection.IsVisible = _vm.ShowWidth;
            AuthorSection.IsVisible = _vm.Mode == AnnotationPropertiesMode.Selection && _vm.CanEditAuthor;
            WidthSlider.Value = _vm.Width;
            OpacitySlider.Value = _vm.OpacityPercent;
            Rebuild(ColorSwatches, _vm.Color, includeNone: false, c => _ = Run(() => _vm.SetColorAsync(c!.Value)));
            if (_vm.ShowFill) Rebuild(FillSwatches, _vm.Fill, includeNone: true, c => _ = Run(() => _vm.SetFillAsync(c)));
        }
        finally { _syncing = false; }
    }

    private static async Task Run(Func<Task> f)
    {
        try { await f(); } catch (Exception) { }
    }

    private void Rebuild(WrapPanel host, PdfColor? current, bool includeNone, Action<PdfColor?> onPick)
    {
        host.Children.Clear();
        if (includeNone) host.Children.Add(MakeSwatch(null, current is null, onPick));
        foreach (var c in _vm!.Swatches) host.Children.Add(MakeSwatch(c, current == c, onPick));
    }

    private static Button MakeSwatch(PdfColor? c, bool selected, Action<PdfColor?> onPick)
    {
        var b = new Button
        {
            Width = 22, Height = 22, Margin = new Thickness(0, 4, 4, 0), Padding = new Thickness(0),
            Background = c is { } v ? new SolidColorBrush(Color.FromRgb(v.R, v.G, v.B)) : Brushes.White,
            BorderBrush = selected ? Brushes.DodgerBlue : new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Content = c is null ? new TextBlock { Text = "∅", FontSize = 11, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center } : null,
        };
        Avalonia.Automation.AutomationProperties.SetName(b, c?.ToHex() ?? "none");
        b.Click += (_, _) => onPick(c);
        return b;
    }
}
