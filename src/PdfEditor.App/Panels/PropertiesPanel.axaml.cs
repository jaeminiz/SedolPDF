using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PdfEditor.App.Resources;
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
        BuildChoices();
        StartEndBox.SelectionChanged += (_, _) => OnEndingPicked(StartEndBox, start: true);
        EndEndBox.SelectionChanged += (_, _) => OnEndingPicked(EndEndBox, start: false);
        CloudCheck.IsCheckedChanged += (_, _) => { if (!_syncing && _vm is not null) _ = Run(() => _vm.SetCloudAsync(CloudCheck.IsChecked == true)); };
        CloudBox.ValueChanged += (_, e) => { if (!_syncing && _vm is not null && e.NewValue is { } v) _ = Run(() => _vm.SetCloudIntensityAsync((float)v)); };
        FontBox.ValueChanged += (_, e) => { if (!_syncing && _vm is not null && e.NewValue is { } v) _ = Run(() => _vm.SetFontSizeAsync((float)v)); };
        AlignBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _vm is not null && AlignBox.SelectedIndex >= 0) _ = Run(() => _vm.SetAlignAsync((PdfEditor.Document.Annotations.TextAlign)AlignBox.SelectedIndex));
        };
        StampTextBtn.Click += (_, _) => { if (_vm is not null) _ = Run(() => _vm.SetStampTextAsync(StampTextBox.Text ?? "")); };
        StampImageBtn.Click += (_, _) => _ = PickStampImageAsync();
        AttachSaveBtn.Click += (_, _) => _ = SaveAttachmentAsync();
        AuthorBox.LostFocus += (_, _) => _ = CommitAuthorAsync();
        AuthorBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = CommitAuthorAsync(); e.Handled = true; } };
        DataContextChanged += (_, _) => Attach();
    }

    internal static readonly (PdfEditor.Document.Annotations.LineEnding E, string Glyph)[] EndingGlyphs =
    [
        (PdfEditor.Document.Annotations.LineEnding.None, "\u2014"), (PdfEditor.Document.Annotations.LineEnding.Square, "\u25A0"),
        (PdfEditor.Document.Annotations.LineEnding.Circle, "\u25CF"), (PdfEditor.Document.Annotations.LineEnding.Diamond, "\u25C6"),
        (PdfEditor.Document.Annotations.LineEnding.OpenArrow, "\u2192"), (PdfEditor.Document.Annotations.LineEnding.ClosedArrow, "\u25B6"),
        (PdfEditor.Document.Annotations.LineEnding.Butt, "|"), (PdfEditor.Document.Annotations.LineEnding.Slash, "/"),
    ];

    // 고정 목록(선 끝 8가지, 정렬 3가지, 기본 스탬프 6종)은 한 번만 채운다.
    private void BuildChoices()
    {
        foreach (var (e, glyph) in EndingGlyphs)
        {
            string text = glyph + "  " + AnnotationPropertiesViewModel.LineEndingName(e);
            StartEndBox.Items.Add(new ComboBoxItem { Content = text, Tag = e });
            EndEndBox.Items.Add(new ComboBoxItem { Content = text, Tag = e });
        }
        AlignBox.Items.Add(new ComboBoxItem { Content = Strings.Align_Left });
        AlignBox.Items.Add(new ComboBoxItem { Content = Strings.Align_Center });
        AlignBox.Items.Add(new ComboBoxItem { Content = Strings.Align_Right });
    }

    private void OnEndingPicked(ComboBox box, bool start)
    {
        if (_syncing || _vm is null || box.SelectedItem is not ComboBoxItem { Tag: PdfEditor.Document.Annotations.LineEnding e }) return;
        _ = Run(() => _vm.SetLineEndingAsync(start, e));
    }

    private async Task PickStampImageAsync()
    {
        if (_vm is null || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Dlg_PickImageTitle,
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Strings.Dlg_FilterImages) { Patterns = ["*.png", "*.jpg", "*.jpeg"] }],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            // 너무 큰 파일은 읽지 않고 빈 배열로 넘겨 보관소의 검증 실패(안내)를 탄다.
            byte[] data = new FileInfo(path).Length > PdfEditor.Document.Annotations.AnnotationAssets.MaxImageBytes ? [] : await File.ReadAllBytesAsync(path);
            await _vm.SetStampImageAsync(data);
        }
        catch (Exception) { _vm.NotifyImageFailed(); }
    }

    private async Task SaveAttachmentAsync()
    {
        if (_vm is null || TopLevel.GetTopLevel(this) is not { } top) return;
        await _vm.SaveSelectedAttachmentAsync(async suggested =>
        {
            var f = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = suggested });
            return f?.TryGetLocalPath();
        });
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
            LineEndSection.IsVisible = _vm.ShowLineEndings;
            CloudSection.IsVisible = _vm.ShowCloud;
            TextSection.IsVisible = _vm.ShowText;
            StampSection.IsVisible = _vm.ShowStamp;
            AttachSection.IsVisible = _vm.ShowAttachment;
            if (_vm.ShowLineEndings) { SelectEnding(StartEndBox, _vm.StartEnding); SelectEnding(EndEndBox, _vm.EndEnding); }
            if (_vm.ShowCloud) { CloudCheck.IsChecked = _vm.CloudOn; CloudBox.Value = (decimal)_vm.CloudIntensity; CloudBox.IsEnabled = _vm.CloudOn; }
            if (_vm.ShowText)
            {
                FontBox.Value = (decimal)_vm.FontSizePt;
                AlignBox.SelectedIndex = (int)_vm.Align;
                Rebuild(TextColorSwatches, _vm.TextColor, includeNone: false, noneSelected: false, c => _ = Run(() => _vm.SetTextColorAsync(c!.Value)));
            }
            if (_vm.ShowStamp) RebuildStampButtons();
            AttachSaveBtn.IsEnabled = _vm.CanSaveAttachment;
            AuthorSection.IsVisible = _vm.Mode == AnnotationPropertiesMode.Selection && _vm.CanEditAuthor;
            WidthSlider.Value = _vm.Width;
            OpacitySlider.Value = _vm.OpacityPercent;
            Rebuild(ColorSwatches, _vm.Color, includeNone: false, noneSelected: false, c => _ = Run(() => _vm.SetColorAsync(c!.Value)));
            if (_vm.ShowFill) Rebuild(FillSwatches, _vm.Fill, includeNone: true, noneSelected: _vm.Fill is null && !_vm.IsFillMixed, c => _ = Run(() => _vm.SetFillAsync(c)));
        }
        finally { _syncing = false; }
    }

    private static void SelectEnding(ComboBox box, PdfEditor.Document.Annotations.LineEnding e)
    {
        foreach (var o in box.Items)
            if (o is ComboBoxItem { Tag: PdfEditor.Document.Annotations.LineEnding x } ci && x == e) { box.SelectedItem = ci; return; }
    }

    private void RebuildStampButtons()
    {
        StampButtons.Children.Clear();
        foreach (var name in _vm!.StampNames)
        {
            var b = new Button
            {
                Content = AnnotationPropertiesViewModel.StampDisplayName(name),
                Margin = new Thickness(0, 4, 4, 0),
                BorderBrush = name == _vm.CurrentStampName ? Brushes.DodgerBlue : null,
                BorderThickness = new Thickness(name == _vm.CurrentStampName ? 2 : 1),
            };
            string n = name;
            b.Click += (_, _) => _ = Run(() => _vm.SetStampBuiltinAsync(n));
            StampButtons.Children.Add(b);
        }
    }

    private static async Task Run(Func<Task> f)
    {
        try { await f(); } catch (Exception) { }
    }

    private void Rebuild(WrapPanel host, PdfColor? current, bool includeNone, bool noneSelected, Action<PdfColor?> onPick)
        => FillColorChoices(host, _vm!.Swatches, current, includeNone, noneSelected, onPick);

    /// <summary>이번 실행에서 "다른 색…"으로 고른 색(최근 것이 앞, 최대 8개). 견본 줄에 이어서 보인다.</summary>
    internal static readonly List<PdfColor> RecentCustom = [];

    /// <summary>견본 줄을 채운다: (없음) → 기본 견본 → 최근 고른 색 → 지금 색(목록에 없으면) → "다른 색…" 단추.</summary>
    internal static void FillColorChoices(WrapPanel host, IReadOnlyList<PdfColor> swatches, PdfColor? current, bool includeNone, bool noneSelected, Action<PdfColor?> onPick)
    {
        host.Children.Clear();
        if (includeNone) host.Children.Add(MakeSwatch(null, noneSelected, onPick));
        foreach (var c in swatches) host.Children.Add(MakeSwatch(c, current == c, onPick));
        foreach (var c in RecentCustom.Where(x => !swatches.Contains(x))) host.Children.Add(MakeSwatch(c, current == c, onPick));
        if (current is { } cur && !swatches.Contains(cur) && !RecentCustom.Contains(cur)) host.Children.Add(MakeSwatch(cur, true, onPick));
        host.Children.Add(MakeMoreButton(host, current, onPick));
    }

    internal static Button MakeMoreButton(Control anchor, PdfColor? current, Action<PdfColor?> onPick)
    {
        var b = new Button
        {
            Width = 22, Height = 22, Margin = new Thickness(0, 4, 4, 0), Padding = new Thickness(0),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)), BorderThickness = new Thickness(1),
            Content = new TextBlock { Text = "+", FontSize = 14, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
        };
        Avalonia.Automation.AutomationProperties.SetName(b, PdfEditor.App.Resources.Strings.Tip_MoreColors);
        ToolTip.SetTip(b, PdfEditor.App.Resources.Strings.Tip_MoreColors);
        b.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(anchor) as Window is not { } owner) return;
            var dlg = new PdfEditor.App.Dialogs.ColorPickerDialog(current ?? new PdfColor(0, 0, 0));
            var picked = await dlg.ShowDialog<PdfColor?>(owner);
            if (picked is not { } c) return;
            RecentCustom.Remove(c);
            RecentCustom.Insert(0, c);
            if (RecentCustom.Count > 8) RecentCustom.RemoveRange(8, RecentCustom.Count - 8);
            onPick(c);
        };
        return b;
    }

    internal static Button MakeSwatch(PdfColor? c, bool selected, Action<PdfColor?> onPick)
    {
        var b = new Button
        {
            Width = 22, Height = 22, Margin = new Thickness(0, 4, 4, 0), Padding = new Thickness(0),
            Background = c is { } v ? new SolidColorBrush(Color.FromRgb(v.R, v.G, v.B)) : Brushes.White,
            BorderBrush = selected ? Brushes.DodgerBlue : new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Content = c is null ? new TextBlock { Text = "∅", FontSize = 11, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center } : null,
        };
        Avalonia.Automation.AutomationProperties.SetName(b, c?.ToHex() ?? PdfEditor.App.Resources.Strings.Prop_Annot_NoFill);
        b.Click += (_, _) => onPick(c);
        return b;
    }
}
