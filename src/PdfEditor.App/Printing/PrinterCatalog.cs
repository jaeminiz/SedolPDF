namespace PdfEditor.App.Printing;

/// <summary>인쇄 대화상자가 프린터에 묻는 것들. 시험에서는 가짜로 바꾼다.</summary>
public interface IPrinterCatalog
{
    IReadOnlyList<string> ListPrinters();
    string? DefaultPrinter();
    PrinterCaps QueryCaps(string printer);
    byte[]? DefaultDevMode(string printer);
    byte[] Apply(string printer, byte[] devMode, PrintOptions o, bool landscape, bool driverCopies, PrinterCaps caps);
    PrintSurface? Measure(string printer, byte[]? devMode);
    byte[]? ShowDriverDialog(IntPtr owner, string printer, byte[] devMode);
}

/// <summary>Windows 프린터(winspool/gdi32).</summary>
public sealed class WinPrinterCatalog : IPrinterCatalog
{
    public IReadOnlyList<string> ListPrinters() => PrinterInterop.ListPrinters();
    public string? DefaultPrinter() => PrinterInterop.DefaultPrinter();
    public PrinterCaps QueryCaps(string printer) => PrinterInterop.QueryCaps(printer);
    public byte[]? DefaultDevMode(string printer) => PrinterInterop.DefaultDevMode(printer);
    public byte[] Apply(string printer, byte[] devMode, PrintOptions o, bool landscape, bool driverCopies, PrinterCaps caps)
        => PrinterInterop.Apply(printer, devMode, o, landscape, driverCopies, caps);
    public PrintSurface? Measure(string printer, byte[]? devMode) => PrinterInterop.Measure(printer, devMode);
    public byte[]? ShowDriverDialog(IntPtr owner, string printer, byte[] devMode) => PrinterInterop.ShowDriverDialog(owner, printer, devMode);
}

/// <summary>대화상자가 돌려주는 인쇄 요청. DevMode는 화면에서 고른 값이 이미 반영된 것이고, SoftwareCopies는 프로그램이 직접 반복할 매수다.</summary>
public sealed record PrintRequest(PrintOptions Options, int[] Pages, string Printer, byte[]? DevMode, int SoftwareCopies);
