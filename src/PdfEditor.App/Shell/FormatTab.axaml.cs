using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PdfEditor.App.Panels;
using PdfEditor.App.Settings;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Shell;

/// <summary>
/// "형식" 탭의 내용 (M3b B5). 값 판단은 모두 속성 패널과 같은 AnnotationPropertiesViewModel과 AnnotationStylesViewModel에 있고
/// 여기서는 그 상태를 보여 주고 같은 메서드를 부를 뿐이다. 권한이 없으면 전체를, 패치가 필요한 속성(선 끝)은 패치를 못 하는 문서에서 끈다.
/// </summary>
public sealed partial class FormatTab : UserControl
{
    private MainWindowViewModel? _main;
    private AnnotationPropertiesViewModel? _props;
    private AnnotationStylesViewModel? _styles;
    private bool _syncing;

    public FormatTab()
    {
        InitializeComponent();
        foreach (var (e, glyph) in PropertiesPanel.EndingGlyphs)
        {
            string text = glyph + "  " + AnnotationPropertiesViewModel.LineEndingName(e);
            StartEndBox.Items.Add(new ComboBoxItem { Content = text, Tag = e });
            EndEndBox.Items.Add(new ComboBoxItem { Content = text, Tag = e });
        }
        StartEndBox.SelectionChanged += (_, _) => OnEndingPicked(StartEndBox, start: true);
        EndEndBox.SelectionChanged += (_, _) => OnEndingPicked(EndEndBox, start: false);
        WidthBox.ValueChanged += (_, e) => { if (!_syncing && _props is not null && e.NewValue is { } v) _props.Width = (float)v; };
        FontBox.ValueChanged += (_, e) => { if (!_syncing && _props is not null && e.NewValue is { } v) _ = Run(() => _props.SetFontSizeAsync((float)v)); };
        SaveStyleBtn.Click += (_, _) => _styles?.SaveCurrentFromInput();
        StyleNameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _styles?.SaveCurrentFromInput(); e.Handled = true; } };
        StyleNameBox.TextChanged += (_, _) => { if (!_syncing && _styles is not null) _styles.NewStyleName = StyleNameBox.Text ?? ""; };
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_main is not null) _main.PropertyChanged -= OnMainChanged;
        _main = DataContext as MainWindowViewModel;
        if (_main is not null) _main.PropertyChanged += OnMainChanged;
        Rebind();
    }

    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ActiveStyles) or nameof(MainWindowViewModel.ActiveAnnotationProperties)) Rebind();
    }

    private void Rebind()
    {
        if (_props is not null) _props.PropertyChanged -= OnModelChanged;
        if (_styles is not null) _styles.PropertyChanged -= OnModelChanged;
        _props = _main?.ActiveAnnotationProperties;
        _styles = _main?.ActiveStyles;
        if (_props is not null) _props.PropertyChanged += OnModelChanged;
        if (_styles is not null) _styles.PropertyChanged += OnModelChanged;
        Sync();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        _syncing = true;
        try
        {
            var p = _props;
            var s = _styles;
            Root.IsEnabled = s?.CanEdit ?? false;
            StyleList.ItemsSource = s?.Available;
            NoStylesHint.IsVisible = s is not null && s.Available.Count == 0;
            if (s is not null && StyleNameBox.Text != s.NewStyleName) StyleNameBox.Text = s.NewStyleName;
            if (p is null) { ColorGroup.IsVisible = LineGroup.IsVisible = TextGroup.IsVisible = false; return; }

            ColorGroup.IsVisible = p.IsActive;
            Rebuild(ColorSwatches, p.Color, c => _ = Run(() => p.SetColorAsync(c!.Value)));

            WidthPart.IsVisible = p.ShowWidth;
            EndingPart.IsVisible = p.ShowLineEndings;
            EndingPart.IsEnabled = s?.CanEditPatchProperties ?? false;   // 선 끝은 저장 때 패치가 필요하다
            LineGroup.IsVisible = p.ShowWidth || p.ShowLineEndings;
            WidthLabel.Text = p.WidthLabel;
            WidthBox.Value = (decimal)p.Width;
            if (p.ShowLineEndings) { SelectEnding(StartEndBox, p.StartEnding); SelectEnding(EndEndBox, p.EndEnding); }

            TextGroup.IsVisible = p.ShowText;
            if (p.ShowText)
            {
                FontLabel.Text = p.FontSizeLabel;
                FontBox.Value = (decimal)p.FontSizePt;
                Rebuild(TextColorSwatches, p.TextColor, c => _ = Run(() => p.SetTextColorAsync(c!.Value)));
            }
        }
        finally { _syncing = false; }
    }

    private void Rebuild(WrapPanel host, PdfColor? current, Action<PdfColor?> onPick)
    {
        host.Children.Clear();
        foreach (var c in _props!.Swatches) host.Children.Add(PropertiesPanel.MakeSwatch(c, current == c, onPick));
    }

    private static void SelectEnding(ComboBox box, LineEnding e)
    {
        foreach (var o in box.Items)
            if (o is ComboBoxItem { Tag: LineEnding x } ci && x == e) { box.SelectedItem = ci; return; }
    }

    private void OnEndingPicked(ComboBox box, bool start)
    {
        if (_syncing || _props is null || box.SelectedItem is not ComboBoxItem { Tag: LineEnding e }) return;
        _ = Run(() => _props.SetLineEndingAsync(start, e));
    }

    private void OnStyleClick(object? sender, RoutedEventArgs e)
    {
        if (_styles is not null && (sender as Control)?.DataContext is AnnotationStyle st) _ = Run(() => _styles.ApplyAsync(st));
    }

    private void OnStyleDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_styles is not null && (sender as Control)?.DataContext is AnnotationStyle st) _styles.Delete(st.Name);
    }

    private static async Task Run(Func<Task> f)
    {
        try { await f(); } catch (Exception) { }
    }
}
