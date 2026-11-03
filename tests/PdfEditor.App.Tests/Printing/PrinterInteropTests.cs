using PdfEditor.App.Printing;
using Xunit.Abstractions;

namespace PdfEditor.App.Tests.Printing;

/// <summary>프린터 목록·능력·DEVMODE. 읽기 전용 조회와 Microsoft Print to PDF 전용 DC 생성만 한다(물리 프린터로 보내지 않음).</summary>
public class PrinterInteropTests(ITestOutputHelper output)
{
    private const string PdfPrinter = "Microsoft Print to PDF";

    [Fact]
    public void ListPrinters_ContainsPdfPrinter_AndDefaultIsInstalledOrNull()
    {
        var list = PrinterInterop.ListPrinters();
        output.WriteLine(string.Join(" | ", list));
        Assert.Contains(PdfPrinter, list);
        var def = PrinterInterop.DefaultPrinter();
        if (def is not null) Assert.Contains(def, list);
    }

    [Fact]
    public void Caps_PdfPrinter_HasA4AndLetter()
    {
        var caps = PrinterInterop.QueryCaps(PdfPrinter);
        output.WriteLine($"papers={caps.Papers.Count} maxCopies={caps.MaxCopies} duplex={caps.CanDuplex} color={caps.IsColor} collate={caps.CanCollate}");
        Assert.Contains(caps.Papers, p => p.Id == 9);    // DMPAPER_A4
        Assert.Contains(caps.Papers, p => p.Id == 1);    // DMPAPER_LETTER
        var a4 = caps.Papers.First(p => p.Id == 9);
        Assert.InRange(a4.WidthMm10, 2090, 2110);
        Assert.InRange(a4.HeightMm10, 2960, 2980);
    }

    [Fact]
    public void DevMode_Apply_LandscapeAndPaper_ChangesDeviceSurface()
    {
        var dm = PrinterInterop.DefaultDevMode(PdfPrinter);
        Assert.NotNull(dm);
        var caps = PrinterInterop.QueryCaps(PdfPrinter);
        var portrait = PrinterInterop.Apply(PdfPrinter, dm!, new PrintOptions(PaperSize: 9), landscape: false, driverCopies: false, caps);
        var land = PrinterInterop.Apply(PdfPrinter, dm!, new PrintOptions(PaperSize: 9), landscape: true, driverCopies: false, caps);
        var sp = PrinterInterop.Measure(PdfPrinter, portrait)!;
        var sl = PrinterInterop.Measure(PdfPrinter, land)!;
        output.WriteLine($"portrait {sp}\nlandscape {sl}");
        Assert.True(sp.Height > sp.Width);
        Assert.True(sl.Width > sl.Height);
        Assert.True(PrinterInterop.Read(land).Landscape);
        var letter = PrinterInterop.Measure(PdfPrinter, PrinterInterop.Apply(PdfPrinter, dm!, new PrintOptions(PaperSize: 1), false, false, caps))!;
        Assert.NotEqual(sp.PhysicalHeight, letter.PhysicalHeight);   // A4 와 Letter 는 세로 길이가 다르다
    }
}
