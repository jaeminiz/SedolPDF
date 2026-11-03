using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Printing;

/// <summary>
/// 대화상자가 문서에서 받는 것. RenderPreview가 null이면 미리보기 없이 옵션만 받는다(시험).
/// 미리보기 그림은 (장, 옵션, 용지 장치 크기, 너비 픽셀, 취소) → 비트맵으로, 출력 사본을 PDFium 전용 스레드에서 그리는 호출이다.
/// </summary>
public sealed record PrintDialogContext(
    IReadOnlyList<(float W, float H)> PageSizes,
    int CurrentIndex,
    IReadOnlyCollection<int> SelectedIndices,
    IPrinterCatalog Catalog,
    PrintOptions Initial,
    Func<PrintSheet, PrintOptions, PrintSurface, int, CancellationToken, Task<PreviewBitmap?>>? RenderPreview);

/// <summary>
/// 인쇄 대화상자(옵션 + 쪽 미리보기). 확인하면 PrintRequest, 취소하면 null.
/// 옵션을 바꾸면 150ms 뒤 미리보기를 백그라운드에서 다시 그린다(그리는 동안 UI는 막히지 않고, 낡은 결과는 버린다).
/// 프린터 목록·능력 조회는 프린터를 바꿀 때만 UI 스레드에서 한다.
/// </summary>
public sealed partial class PrintDialog : Window
{
    private const int PreviewWidthPx = 520;

    private readonly PrintDialogContext _ctx;
    private CancellationTokenSource? _debounce;
    private readonly Dictionary<string, PrintSurface?> _surfaces = [];
    private CancellationTokenSource _cts = new();
    private int _gen;
    private bool _loading = true;

    private string? _printer;
    private PrinterCaps _caps = new([], 1, false, true, false);
    private byte[]? _baseDm;
    private int _dmVersion;
    private int[] _pages = [];
    private bool _valid;
    private IReadOnlyList<PrintSheet> _sheets = [];
    private int _sheetIndex;

    public PrintDialog() : this(new PrintDialogContext([], 0, [], new WinPrinterCatalog(), new PrintOptions(), null)) { }

    public PrintDialog(PrintDialogContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        ParityBox.ItemsSource = new[] { Strings.Print_ParityAll, Strings.Print_ParityOdd, Strings.Print_ParityEven };
        OrientBox.ItemsSource = new[] { Strings.Print_OrientAuto, Strings.Print_OrientPortrait, Strings.Print_OrientLandscape };
        DuplexBox.ItemsSource = new[] { Strings.Print_DuplexOff, Strings.Print_DuplexLong, Strings.Print_DuplexShort };
        PerSheetBox.ItemsSource = new[] { Strings.Print_PerSheet1, Strings.Print_PerSheet2, Strings.Print_PerSheet4 };

        var printers = ctx.Catalog.ListPrinters();
        PrinterBox.ItemsSource = printers;
        NoPrinterText.IsVisible = printers.Count == 0;
        PrinterBox.IsEnabled = PropsButton.IsEnabled = printers.Count > 0;

        ApplyInitial(ctx.Initial.Normalized(), printers);

        foreach (var r in new[] { RangeAll, RangeCurrent, RangeSelected, RangeCustom, FitRadio, ActualRadio, PercentRadio, WithAnnotRadio, NoAnnotRadio })
            r.IsCheckedChanged += (_, _) => Changed();
        foreach (var c in new[] { CollateBox, CenterBox, AutoRotateBox, GrayBox })
            c.IsCheckedChanged += (_, _) => Changed();
        foreach (var c in new[] { ParityBox, PaperBox, OrientBox, DuplexBox, PerSheetBox })
            c.SelectionChanged += (_, _) => Changed();
        CopiesBox.ValueChanged += (_, _) => Changed();
        PercentBox.ValueChanged += (_, _) => Changed();
        RangeText.TextChanged += (_, _) => Changed();
        PrinterBox.SelectionChanged += (_, _) => { if (!_loading) { LoadPrinter(PrinterBox.SelectedItem as string, null); Changed(); } };
        PropsButton.Click += (_, _) => ShowDriverDialog();
        PrevButton.Click += (_, _) => Navigate(-1);
        NextButton.Click += (_, _) => Navigate(1);
        OkButton.Click += (_, _) => { if (BuildRequest() is { } req) Close(req); };
        CancelButton.Click += (_, _) => Close(null);
        Closed += (_, _) => { _debounce?.Cancel(); _cts.Cancel(); };

        _loading = false;
        Changed();
    }

    // ---- 초기값 ----

    private void ApplyInitial(PrintOptions o, IReadOnlyList<string> printers)
    {
        bool hasSelection = _ctx.SelectedIndices.Count > 0;
        RangeSelected.IsEnabled = hasSelection;
        var mode = o.Range;
        if (mode == PrintRangeMode.Selected && !hasSelection) mode = PrintRangeMode.All;
        // 썸네일에서 여러 쪽을 골라 둔 채 인쇄를 눌렀다면 선택한 쪽을 먼저 보여 준다.
        if (mode == PrintRangeMode.All && _ctx.SelectedIndices.Count > 1) mode = PrintRangeMode.Selected;
        RangeAll.IsChecked = mode == PrintRangeMode.All;
        RangeCurrent.IsChecked = mode == PrintRangeMode.Current;
        RangeSelected.IsChecked = mode == PrintRangeMode.Selected;
        RangeCustom.IsChecked = mode == PrintRangeMode.Custom;
        RangeText.Text = o.RangeText;
        ParityBox.SelectedIndex = (int)o.Parity;
        CopiesBox.Value = o.Copies;
        CollateBox.IsChecked = o.Collate;
        OrientBox.SelectedIndex = (int)o.Orientation;
        DuplexBox.SelectedIndex = (int)o.Duplex;
        FitRadio.IsChecked = o.Scaling == PrintScaling.FitToPaper;
        ActualRadio.IsChecked = o.Scaling == PrintScaling.ActualSize;
        PercentRadio.IsChecked = o.Scaling == PrintScaling.Percent;
        PercentBox.Value = o.ScalePercent;
        CenterBox.IsChecked = o.Center;
        AutoRotateBox.IsChecked = o.AutoRotate;
        PerSheetBox.SelectedIndex = o.PagesPerSheet == 4 ? 2 : o.PagesPerSheet == 2 ? 1 : 0;
        WithAnnotRadio.IsChecked = o.WithAnnotations;
        NoAnnotRadio.IsChecked = !o.WithAnnotations;
        GrayBox.IsChecked = o.Grayscale;

        string? want = o.PrinterName is { } pn && printers.Contains(pn) ? pn : _ctx.Catalog.DefaultPrinter();
        if (want is null || !printers.Contains(want)) want = printers.FirstOrDefault();
        PrinterBox.SelectedItem = want;
        LoadPrinter(want, o.PaperSize);
    }

    private void LoadPrinter(string? printer, short? paperId)
    {
        _printer = printer;
        _dmVersion++;
        _surfaces.Clear();
        bool was = _loading;
        _loading = true;
        try
        {
            if (printer is null)
            {
                _caps = new([], 1, false, true, false);
                _baseDm = null;
            }
            else
            {
                try { _caps = _ctx.Catalog.QueryCaps(printer); _baseDm = _ctx.Catalog.DefaultDevMode(printer); }
                catch (Exception) { _caps = new([], 1, false, true, false); _baseDm = null; }
            }
            short keep = paperId ?? SelectedPaperId();
            PaperBox.ItemsSource = new[] { Strings.Print_PaperDefault }.Concat(_caps.Papers.Select(p => p.Name)).ToArray();
            int idx = _caps.Papers.ToList().FindIndex(p => p.Id == keep);
            PaperBox.SelectedIndex = idx < 0 ? 0 : idx + 1;
            DuplexBox.IsEnabled = _caps.CanDuplex;
            if (!_caps.CanDuplex) DuplexBox.SelectedIndex = 0;
        }
        finally { _loading = was; }
    }

    private short SelectedPaperId()
    {
        int i = PaperBox.SelectedIndex - 1;
        return i >= 0 && i < _caps.Papers.Count ? _caps.Papers[i].Id : (short)0;
    }

    // ---- 값 읽기 ----

    private PrintOptions ReadOptions() => new PrintOptions(
        WithAnnotations: WithAnnotRadio.IsChecked == true,
        Scaling: PercentRadio.IsChecked == true ? PrintScaling.Percent : ActualRadio.IsChecked == true ? PrintScaling.ActualSize : PrintScaling.FitToPaper,
        AutoRotate: AutoRotateBox.IsChecked == true,
        Range: RangeCurrent.IsChecked == true ? PrintRangeMode.Current : RangeSelected.IsChecked == true ? PrintRangeMode.Selected : RangeCustom.IsChecked == true ? PrintRangeMode.Custom : PrintRangeMode.All,
        RangeText: RangeText.Text ?? "",
        Parity: (PrintParity)Math.Max(0, ParityBox.SelectedIndex),
        PrinterName: _printer,
        Copies: (int)(CopiesBox.Value ?? 1),
        Collate: CollateBox.IsChecked == true,
        Duplex: (PrintDuplex)Math.Max(0, DuplexBox.SelectedIndex),
        Orientation: (PrintOrientation)Math.Max(0, OrientBox.SelectedIndex),
        PaperSize: SelectedPaperId(),
        ScalePercent: (int)(PercentBox.Value ?? 100),
        Center: CenterBox.IsChecked == true,
        PagesPerSheet: PerSheetBox.SelectedIndex == 2 ? 4 : PerSheetBox.SelectedIndex == 1 ? 2 : 1,
        Grayscale: GrayBox.IsChecked == true).Normalized();

    private bool LandscapeFor(PrintOptions o, int[] pages)
    {
        var first = pages.Length > 0 && pages[0] < _ctx.PageSizes.Count ? _ctx.PageSizes[pages[0]] : (W: 595f, H: 842f);
        return PrintLayout.IsLandscapeSheet(o.Orientation, o.PagesPerSheet, first.W, first.H);
    }

    // ---- 변경 반영 ----

    private void Changed()
    {
        if (_loading) return;
        PercentBox.IsEnabled = PercentRadio.IsChecked == true && PercentRadio.IsEnabled;
        RangeText.IsEnabled = RangeCustom.IsChecked == true;
        CollateBox.IsEnabled = (CopiesBox.Value ?? 1) > 1;
        bool nup = PerSheetBox.SelectedIndex > 0;
        FitRadio.IsEnabled = ActualRadio.IsEnabled = PercentRadio.IsEnabled = CenterBox.IsEnabled = !nup;
        PercentBox.IsEnabled = PercentBox.IsEnabled && !nup;

        var o = ReadOptions();
        bool ok = PrintPageSelection.TryResolve(o, _ctx.PageSizes.Count, _ctx.CurrentIndex, _ctx.SelectedIndices, out _pages);
        RangeError.IsVisible = !ok;
        _valid = ok && _pages.Length > 0 && _printer is not null;
        OkButton.IsEnabled = _valid;
        _sheets = ok ? PrintPlan.Build(_pages, o.PagesPerSheet, 1, true, false) : [];
        SummaryText.Text = ok ? string.Format(Strings.Print_Summary, _pages.Length, _sheets.Count) : "";
        _sheetIndex = Math.Clamp(_sheetIndex, 0, Math.Max(0, _sheets.Count - 1));
        UpdateNav();
        Schedule();
    }

    private void Navigate(int delta)
    {
        int n = Math.Clamp(_sheetIndex + delta, 0, Math.Max(0, _sheets.Count - 1));
        if (n == _sheetIndex) return;
        _sheetIndex = n;
        UpdateNav();
        Schedule();
    }

    private void UpdateNav()
    {
        PrevButton.IsEnabled = _sheetIndex > 0;
        NextButton.IsEnabled = _sheetIndex < _sheets.Count - 1;
        SheetText.Text = _sheets.Count == 0 ? "" : string.Format(Strings.Print_SheetOf, _sheetIndex + 1, _sheets.Count);
    }

    /// <summary>화면의 값을 확정한다. 인쇄할 쪽이 없거나 범위가 틀리면 null.</summary>
    public PrintRequest? BuildRequest()
    {
        Changed();
        if (!_valid || _printer is null) return null;
        var o = ReadOptions();
        bool driverCopies = o.Copies > 1 && _caps.MaxCopies >= o.Copies;
        byte[]? dm = _baseDm is null ? null : _ctx.Catalog.Apply(_printer, _baseDm, o, LandscapeFor(o, _pages), driverCopies, _caps);
        return new PrintRequest(o, _pages, _printer, dm, driverCopies ? 1 : o.Copies);
    }

    // ---- 프린터 속성 ----

    private void ShowDriverDialog()
    {
        if (_printer is null || _baseDm is null) return;
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        byte[]? dm;
        try { dm = _ctx.Catalog.ShowDriverDialog(hwnd, _printer, _baseDm); }
        catch (Exception) { return; }
        if (dm is null) return;
        _baseDm = dm;
        _dmVersion++;
        _surfaces.Clear();
        // 드라이버 대화상자에서 바꾼 값을 화면에 되돌려 보인다.
        var (landscape, paper, duplex, mono, copies, collate) = PrinterInterop.Read(dm);
        _loading = true;
        try
        {
            if (landscape) OrientBox.SelectedIndex = (int)PrintOrientation.Landscape;
            else if (OrientBox.SelectedIndex == (int)PrintOrientation.Landscape) OrientBox.SelectedIndex = (int)PrintOrientation.Portrait;
            int pi = _caps.Papers.ToList().FindIndex(p => p.Id == paper);
            if (pi >= 0) PaperBox.SelectedIndex = pi + 1;
            if (_caps.CanDuplex) DuplexBox.SelectedIndex = (int)duplex;
            if (mono) GrayBox.IsChecked = true;
            CopiesBox.Value = copies;
            CollateBox.IsChecked = collate;
        }
        finally { _loading = false; }
        Changed();
    }

    // ---- 미리보기 ----

    private void Schedule()
    {
        var old = _debounce;
        var cur = _debounce = new CancellationTokenSource();
        old?.Cancel();
        _ = DebounceAsync(cur.Token);
    }

    // 150ms 동안 더 바뀌지 않으면 UI 스레드에서 그리기를 시작한다.
    private async Task DebounceAsync(CancellationToken ct)
    {
        try { await Task.Delay(150, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try { await Dispatcher.UIThread.InvokeAsync(RenderCurrentAsync); }
        catch (Exception) { /* 닫힌 뒤의 늦은 호출 */ }
    }

    // 첫 await 앞까지만 UI 스레드에서 상태를 읽고, 그 뒤는 백그라운드로 돌려 결과만 UI 스레드에 올린다.
    private async Task RenderCurrentAsync()
    {
        _cts.Cancel();
        if (_ctx.RenderPreview is null) return;
        if (!_valid || _sheets.Count == 0 || _printer is null)
        {
            ShowStatus(_printer is null ? Strings.Print_NoPrinter : Strings.Print_PreviewNone);
            return;
        }
        var cts = _cts = new CancellationTokenSource();
        int gen = ++_gen;
        var o = ReadOptions();
        var sheet = _sheets[_sheetIndex];
        bool landscape = LandscapeFor(o, _pages);
        string printer = _printer;
        var baseDm = _baseDm;
        var caps = _caps;
        string key = $"{printer}|{_dmVersion}|{o.PaperSize}|{landscape}";
        bool haveCached = _surfaces.TryGetValue(key, out var cached);
        if (!haveCached) PreviewStatus.Text = Strings.Print_PreviewLoading;
        void Ui(Action a) => Dispatcher.UIThread.Post(() => { if (gen == _gen) a(); });
        try
        {
            var surf = haveCached ? cached : await Task.Run(() =>
            {
                var dm = baseDm is null ? null : _ctx.Catalog.Apply(printer, baseDm, o, landscape, false, caps);
                return _ctx.Catalog.Measure(printer, dm);
            }, cts.Token).ConfigureAwait(false);
            Ui(() => _surfaces[key] = surf);
            if (surf is null) { Ui(() => ShowStatus(Strings.Print_PreviewFailed)); return; }
            var bmp = await _ctx.RenderPreview(sheet, o, surf, PreviewWidthPx, cts.Token).ConfigureAwait(false);
            Ui(() => { if (bmp is null) ShowStatus(Strings.Print_PreviewFailed); else ShowBitmap(bmp); });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Ui(() => ShowStatus(Strings.Print_PreviewFailed)); }
    }

    private void ShowStatus(string text)
    {
        PreviewImage.Source = null;
        PreviewStatus.Text = text;
    }

    private void ShowBitmap(PreviewBitmap p)
    {
        var wb = new WriteableBitmap(new PixelSize(p.Width, p.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = wb.Lock())
        {
            for (int y = 0; y < p.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(p.Bgra, y * p.Width * 4, fb.Address + y * fb.RowBytes, p.Width * 4);
        }
        PreviewImage.Source = wb;
        PreviewStatus.Text = "";
    }
}
