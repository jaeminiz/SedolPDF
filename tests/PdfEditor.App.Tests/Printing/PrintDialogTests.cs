using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PdfEditor.App.Printing;
using PdfEditor.App.Tests.Viewer.Annotations;

namespace PdfEditor.App.Tests.Printing;

/// <summary>인쇄 대화상자(헤드리스): 쪽 범위, 매수 처리 방식, 입력 검증, 미리보기 갱신. 프린터는 가짜.</summary>
public class PrintDialogTests
{
    private sealed class FakeCatalog(int maxCopies = 1, bool duplex = true) : IPrinterCatalog
    {
        public readonly List<(PrintOptions O, bool Landscape, bool DriverCopies)> Applied = [];
        public IReadOnlyList<string> ListPrinters() => ["Fake A", "Fake B"];
        public string? DefaultPrinter() => "Fake B";
        public PrinterCaps QueryCaps(string printer) => new([new PaperInfo(9, "A4", 2100, 2970), new PaperInfo(1, "Letter", 2159, 2794)], maxCopies, duplex, true, true);
        public byte[]? DefaultDevMode(string printer) => [1, 2, 3];
        public byte[] Apply(string printer, byte[] devMode, PrintOptions o, bool landscape, bool driverCopies, PrinterCaps caps)
        { lock (Applied) Applied.Add((o, landscape, driverCopies)); return [9, 9]; }
        public PrintSurface? Measure(string printer, byte[]? devMode) => new(4960, 7016, 600, 600, 4961, 7016, 0, 0);
        public byte[]? ShowDriverDialog(IntPtr owner, string printer, byte[] devMode) => null;
    }

    private static PrintDialogContext Ctx(IPrinterCatalog cat, int pages = 10, int current = 3, int[]? selected = null, PrintOptions? initial = null,
        Func<PrintSheet, PrintOptions, PrintSurface, int, CancellationToken, Task<PreviewBitmap?>>? render = null)
        => new(Enumerable.Range(0, pages).Select(_ => (595f, 842f)).ToList(), current, selected ?? [], cat, initial ?? new PrintOptions(), render);

    private static T Run<T>(Func<T> f) => HeadlessUi.Run(f);

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Find<T>(PrintDialog d, string name) where T : Control => d.FindControl<T>(name) ?? throw new InvalidOperationException(name);

    [Fact]
    public void Defaults_AllPages_DefaultPrinter_SelectedDisabledWithoutSelection()
    {
        var req = Run(() =>
        {
            var d = new PrintDialog(Ctx(new FakeCatalog()));
            Assert.False(Find<RadioButton>(d, "RangeSelected").IsEnabled);
            Assert.False(Find<TextBox>(d, "RangeText").IsEnabled);
            return d.BuildRequest();
        });
        Assert.NotNull(req);
        Assert.Equal("Fake B", req!.Printer);
        Assert.Equal(Enumerable.Range(0, 10), req.Pages);
        Assert.Equal(1, req.SoftwareCopies);
        Assert.Equal([9, 9], req.DevMode);
    }

    [Fact]
    public void Range_Current_Selected_Custom_Parity()
    {
        var (cur, sel, custom, oddSel) = Run(() =>
        {
            var d = new PrintDialog(Ctx(new FakeCatalog(), selected: [2, 3, 4]));
            Assert.True(Find<RadioButton>(d, "RangeSelected").IsChecked);   // 여러 쪽을 골라 두었으면 선택한 쪽이 먼저
            var s = d.BuildRequest()!.Pages;
            Find<RadioButton>(d, "RangeCurrent").IsChecked = true;
            var c = d.BuildRequest()!.Pages;
            Find<RadioButton>(d, "RangeCustom").IsChecked = true;
            Find<TextBox>(d, "RangeText").Text = "1-3, 5";
            var cu = d.BuildRequest()!.Pages;
            Find<RadioButton>(d, "RangeSelected").IsChecked = true;
            Find<ComboBox>(d, "ParityBox").SelectedIndex = 1;   // 홀수
            var o = d.BuildRequest()!.Pages;
            return (c, s, cu, o);
        });
        Assert.Equal([3], cur);
        Assert.Equal([2, 3, 4], sel);
        Assert.Equal([0, 1, 2, 4], custom);
        Assert.Equal([2, 4], oddSel);   // 3쪽·5쪽
    }

    [Fact]
    public void Custom_Invalid_ShowsError_AndBlocksPrint()
    {
        var (req, errVisible, okEnabled) = Run(() =>
        {
            var d = new PrintDialog(Ctx(new FakeCatalog()));
            Find<RadioButton>(d, "RangeCustom").IsChecked = true;
            Find<TextBox>(d, "RangeText").Text = "3-1";
            var r = d.BuildRequest();
            return (r, Find<TextBlock>(d, "RangeError").IsVisible, Find<Button>(d, "OkButton").IsEnabled);
        });
        Assert.Null(req);
        Assert.True(errVisible);
        Assert.False(okEnabled);
    }

    [Fact]
    public void Copies_DriverHandles_WhenSupported_ElseSoftware()
    {
        var (drv, sw, appliedDriver) = Run(() =>
        {
            var cat1 = new FakeCatalog(maxCopies: 99);
            var d1 = new PrintDialog(Ctx(cat1));
            Find<NumericUpDown>(d1, "CopiesBox").Value = 3;
            var r1 = d1.BuildRequest()!;
            var d2 = new PrintDialog(Ctx(new FakeCatalog(maxCopies: 1)));
            Find<NumericUpDown>(d2, "CopiesBox").Value = 3;
            var r2 = d2.BuildRequest()!;
            return (r1.SoftwareCopies, r2.SoftwareCopies, cat1.Applied.Last().DriverCopies);
        });
        Assert.Equal(1, drv);
        Assert.True(appliedDriver);
        Assert.Equal(3, sw);
    }

    [Fact]
    public void TwoUp_AutoOrientation_IsLandscape_AndScaleDisabled_AndDuplexPassedThrough()
    {
        var cat = new FakeCatalog();
        var (req, fitEnabled) = Run(() =>
        {
            var d = new PrintDialog(Ctx(cat));
            Find<ComboBox>(d, "PerSheetBox").SelectedIndex = 1;
            Find<ComboBox>(d, "DuplexBox").SelectedIndex = 1;
            var r = d.BuildRequest()!;
            return (r, Find<RadioButton>(d, "FitRadio").IsEnabled);
        });
        Assert.Equal(2, req.Options.PagesPerSheet);
        Assert.Equal(PrintDuplex.LongEdge, req.Options.Duplex);
        Assert.True(cat.Applied.Last().Landscape);
        Assert.False(fitEnabled);
    }

    [Fact]
    public void Duplex_Disabled_WhenPrinterCannot()
    {
        var enabled = Run(() => Find<ComboBox>(new PrintDialog(Ctx(new FakeCatalog(duplex: false))), "DuplexBox").IsEnabled);
        Assert.False(enabled);
    }

    [Fact]
    public void Preview_RendersInBackground_AndUpdatesOnOptionChange_AndNavigation()
    {
        var calls = new List<(PrintSheet Sheet, PrintOptions O)>();
        Task<PreviewBitmap?> Render(PrintSheet sheet, PrintOptions o, PrintSurface surf, int width, CancellationToken ct)
        {
            lock (calls) calls.Add((sheet, o));
            return Task.Run<PreviewBitmap?>(() => new PreviewBitmap(8, 10, new byte[8 * 10 * 4]), ct);
        }
        int Count() { lock (calls) return calls.Count; }

        var d = Run(() => new PrintDialog(Ctx(new FakeCatalog(), pages: 4, render: Render)));
        Run(() => { Pump(() => Count() >= 1); return 0; });
        Assert.NotNull(Run(() => Find<Image>(d, "PreviewImage").Source));
        Assert.Equal(new[] { 0 }, calls[0].Sheet.Cells.Select(c => c!.Value));

        // 다음 장
        Run(() => { Click(Find<Button>(d, "NextButton")); Pump(() => Count() >= 2); return 0; });
        Assert.Equal(1, calls[^1].Sheet.Cells[0]);
        // 옵션 변경(흑백) → 같은 장을 다시 그린다
        int before = Count();
        Run(() => { Find<CheckBox>(d, "GrayBox").IsChecked = true; Pump(() => Count() > before); return 0; });
        Assert.True(calls[^1].O.Grayscale);
        Assert.Equal(1, calls[^1].Sheet.Cells[0]);
        // 쪽 모아 찍기 2 → 장 수가 줄고 처음 장으로
        Run(() => { Find<ComboBox>(d, "PerSheetBox").SelectedIndex = 1; Pump(() => calls[^1].O.PagesPerSheet == 2); return 0; });
        Assert.Equal(2, calls[^1].Sheet.Cells.Length);
        Run(() => { d.Close(); return 0; });
    }

    private static void Pump(Func<bool> done)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) { Dispatcher.UIThread.RunJobs(); return; }
            Thread.Sleep(15);
        }
        Assert.Fail("timed out");
    }
}
