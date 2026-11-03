using System.Runtime.InteropServices;

namespace PdfEditor.App.Printing;

/// <summary>프린터가 지원하는 용지 하나. WidthMm10/HeightMm10은 0.1mm 단위(세로 기준).</summary>
public sealed record PaperInfo(short Id, string Name, int WidthMm10, int HeightMm10);

/// <summary>프린터 능력. MaxCopies가 1이면 드라이버가 매수를 처리하지 못하는 것이라 프로그램이 직접 반복한다.</summary>
public sealed record PrinterCaps(IReadOnlyList<PaperInfo> Papers, int MaxCopies, bool CanDuplex, bool IsColor, bool CanCollate);

/// <summary>
/// 프린터 목록·능력·DEVMODE(winspool.drv). DEVMODE는 바이트 배열로 다룬다(공개 앞부분 220바이트 + 드라이버 전용 뒷부분).
/// 값은 드라이버가 정리한 DEVMODE를 바탕으로 우리가 쓰는 필드만 덮어쓴 뒤 다시 한 번 DocumentProperties에 넣어 합친다.
/// </summary>
public static partial class PrinterInterop
{
    // DEVMODEW 필드 위치(바이트)
    private const int OffFields = 72, OffOrientation = 76, OffPaperSize = 78, OffCopies = 86, OffColor = 92, OffDuplex = 94, OffCollate = 100;
    private const int DevModePublicSize = 220;

    private const uint DM_ORIENTATION = 0x1, DM_PAPERSIZE = 0x2, DM_COPIES = 0x100, DM_COLOR = 0x800, DM_DUPLEX = 0x1000, DM_COLLATE = 0x8000;
    private const uint DM_OUT_BUFFER = 2, DM_IN_PROMPT = 4, DM_IN_BUFFER = 8;
    private const short DMORIENT_PORTRAIT = 1, DMORIENT_LANDSCAPE = 2;
    private const short DMDUP_SIMPLEX = 1, DMDUP_VERTICAL = 2, DMDUP_HORIZONTAL = 3;
    private const short DMCOLOR_MONOCHROME = 1, DMCOLOR_COLOR = 2;
    private const ushort DC_PAPERS = 2, DC_PAPERSIZE = 3, DC_DUPLEX = 7, DC_COPIES = 18, DC_PAPERNAMES = 16, DC_COLORDEVICE = 32, DC_COLLATE = 22;

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(IntPtr handle);

    [LibraryImport("winspool.drv", EntryPoint = "DocumentPropertiesW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int DocumentProperties(IntPtr hwnd, IntPtr hPrinter, string deviceName, byte[]? output, byte[]? input, uint mode);

    [LibraryImport("winspool.drv", EntryPoint = "DocumentPropertiesW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int DocumentPropertiesSize(IntPtr hwnd, IntPtr hPrinter, string deviceName, IntPtr output, IntPtr input, uint mode);

    [LibraryImport("winspool.drv", EntryPoint = "EnumPrintersW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumPrinters(uint flags, string? name, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);

    [LibraryImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDefaultPrinter(char[]? buffer, ref uint size);

    [LibraryImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeviceCapabilities(string device, string? port, ushort capability, IntPtr output, IntPtr devMode);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDCW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateDcWithMode(string? driver, string? device, string? output, byte[]? devMode);

    // ---- 프린터 목록 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTER_INFO_4 { public IntPtr pPrinterName, pServerName; public uint Attributes; }

    /// <summary>이 PC에 설치된(로컬 + 연결된) 프린터 이름. 실패하면 빈 목록.</summary>
    public static IReadOnlyList<string> ListPrinters()
    {
        const uint flags = 0x2 | 0x4;   // PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS
        EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out uint needed, out _);
        if (needed == 0) return [];
        IntPtr buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinters(flags, null, 4, buf, needed, out _, out uint count)) return [];
            var list = new List<string>();
            int size = Marshal.SizeOf<PRINTER_INFO_4>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<PRINTER_INFO_4>(buf + i * size);
                if (Marshal.PtrToStringUni(info.pPrinterName) is { Length: > 0 } n) list.Add(n);
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static string? DefaultPrinter()
    {
        uint size = 0;
        GetDefaultPrinter(null, ref size);
        if (size == 0) return null;
        var buf = new char[size];
        return GetDefaultPrinter(buf, ref size) ? new string(buf, 0, (int)size - 1) : null;
    }

    // ---- 능력 ----

    public static PrinterCaps QueryCaps(string printer)
    {
        var papers = new List<PaperInfo>();
        int n = DeviceCapabilities(printer, null, DC_PAPERS, IntPtr.Zero, IntPtr.Zero);
        if (n > 0)
        {
            IntPtr ids = Marshal.AllocHGlobal(n * 2), names = Marshal.AllocHGlobal(n * 64 * 2), sizes = Marshal.AllocHGlobal(n * 8);
            try
            {
                foreach (var (ptr, len) in new[] { (ids, n * 2), (names, n * 128), (sizes, n * 8) })
                    Marshal.Copy(new byte[len], 0, ptr, len);
                int a = DeviceCapabilities(printer, null, DC_PAPERS, ids, IntPtr.Zero);
                int b = DeviceCapabilities(printer, null, DC_PAPERNAMES, names, IntPtr.Zero);
                int c = DeviceCapabilities(printer, null, DC_PAPERSIZE, sizes, IntPtr.Zero);
                if (a == n && b == n && c == n)
                    for (int i = 0; i < n; i++)
                    {
                        short id = Marshal.ReadInt16(ids, i * 2);
                        string name = Marshal.PtrToStringUni(names + i * 128, 64).Split('\0')[0];
                        int w = Marshal.ReadInt32(sizes, i * 8), h = Marshal.ReadInt32(sizes, i * 8 + 4);
                        if (id > 0 && name.Length > 0 && w > 0 && h > 0) papers.Add(new PaperInfo(id, name, w, h));
                    }
            }
            finally { Marshal.FreeHGlobal(ids); Marshal.FreeHGlobal(names); Marshal.FreeHGlobal(sizes); }
        }
        int copies = DeviceCapabilities(printer, null, DC_COPIES, IntPtr.Zero, IntPtr.Zero);
        return new PrinterCaps(papers, Math.Max(1, copies),
            DeviceCapabilities(printer, null, DC_DUPLEX, IntPtr.Zero, IntPtr.Zero) == 1,
            DeviceCapabilities(printer, null, DC_COLORDEVICE, IntPtr.Zero, IntPtr.Zero) == 1,
            DeviceCapabilities(printer, null, DC_COLLATE, IntPtr.Zero, IntPtr.Zero) == 1);
    }

    // ---- DEVMODE ----

    /// <summary>프린터의 기본 DEVMODE. 열 수 없으면 null.</summary>
    public static byte[]? DefaultDevMode(string printer)
    {
        if (!OpenPrinter(printer, out IntPtr h, IntPtr.Zero)) return null;
        try
        {
            int size = DocumentPropertiesSize(IntPtr.Zero, h, printer, IntPtr.Zero, IntPtr.Zero, 0);
            if (size < DevModePublicSize) return null;
            var dm = new byte[size];
            return DocumentProperties(IntPtr.Zero, h, printer, dm, null, DM_OUT_BUFFER) == 1 ? dm : null;   // IDOK = 1
        }
        finally { ClosePrinter(h); }
    }

    /// <summary>드라이버 속성 대화상자(트레이·화질 등 드라이버 전용 값). 확인하면 갱신된 DEVMODE, 취소·실패하면 null.</summary>
    public static byte[]? ShowDriverDialog(IntPtr owner, string printer, byte[] devMode)
    {
        if (!OpenPrinter(printer, out IntPtr h, IntPtr.Zero)) return null;
        try
        {
            var inout = (byte[])devMode.Clone();
            return DocumentProperties(owner, h, printer, inout, inout, DM_IN_BUFFER | DM_OUT_BUFFER | DM_IN_PROMPT) == 1 ? inout : null;
        }
        finally { ClosePrinter(h); }
    }

    /// <summary>화면에서 고른 값(방향·용지·양면·흑백·매수·한 부씩)을 DEVMODE에 쓰고 드라이버에 다시 넘겨 합친다. 합치지 못하면 쓴 그대로 돌려준다.</summary>
    /// <param name="driverCopies">true면 매수·한 부씩을 드라이버에 맡긴다(프로그램은 1부만 보냄).</param>
    public static byte[] Apply(string printer, byte[] devMode, PrintOptions o, bool landscape, bool driverCopies, PrinterCaps caps)
    {
        var dm = (byte[])devMode.Clone();
        uint fields = BitConverter.ToUInt32(dm, OffFields);
        void Put(int off, short v) { BitConverter.TryWriteBytes(dm.AsSpan(off, 2), v); }

        Put(OffOrientation, landscape ? DMORIENT_LANDSCAPE : DMORIENT_PORTRAIT); fields |= DM_ORIENTATION;
        if (o.PaperSize > 0 && caps.Papers.Any(p => p.Id == o.PaperSize)) { Put(OffPaperSize, o.PaperSize); fields |= DM_PAPERSIZE; }
        Put(OffDuplex, o.Duplex switch { PrintDuplex.LongEdge => DMDUP_VERTICAL, PrintDuplex.ShortEdge => DMDUP_HORIZONTAL, _ => DMDUP_SIMPLEX });
        fields |= DM_DUPLEX;
        if (caps.IsColor) { Put(OffColor, o.Grayscale ? DMCOLOR_MONOCHROME : DMCOLOR_COLOR); fields |= DM_COLOR; }
        Put(OffCopies, (short)(driverCopies ? Math.Min(o.Copies, caps.MaxCopies) : 1)); fields |= DM_COPIES;
        Put(OffCollate, (short)(driverCopies && o.Collate ? 1 : 0)); fields |= DM_COLLATE;
        BitConverter.TryWriteBytes(dm.AsSpan(OffFields, 4), fields);

        if (!OpenPrinter(printer, out IntPtr h, IntPtr.Zero)) return dm;
        try
        {
            var merged = new byte[dm.Length];
            return DocumentProperties(IntPtr.Zero, h, printer, merged, dm, DM_IN_BUFFER | DM_OUT_BUFFER) == 1 ? merged : dm;
        }
        finally { ClosePrinter(h); }
    }

    /// <summary>DEVMODE에서 화면에 보일 값을 읽는다(드라이버 대화상자를 닫은 뒤 화면을 맞출 때).</summary>
    public static (bool Landscape, short Paper, PrintDuplex Duplex, bool Mono, int Copies, bool Collate) Read(byte[] dm)
    {
        uint f = BitConverter.ToUInt32(dm, OffFields);
        short S(int off) => BitConverter.ToInt16(dm, off);
        bool land = (f & DM_ORIENTATION) != 0 && S(OffOrientation) == DMORIENT_LANDSCAPE;
        short paper = (f & DM_PAPERSIZE) != 0 ? S(OffPaperSize) : (short)0;
        var duplex = (f & DM_DUPLEX) == 0 ? PrintDuplex.Off : S(OffDuplex) switch { DMDUP_VERTICAL => PrintDuplex.LongEdge, DMDUP_HORIZONTAL => PrintDuplex.ShortEdge, _ => PrintDuplex.Off };
        bool mono = (f & DM_COLOR) != 0 && S(OffColor) == DMCOLOR_MONOCHROME;
        int copies = (f & DM_COPIES) != 0 ? Math.Max(1, (int)S(OffCopies)) : 1;
        bool collate = (f & DM_COLLATE) != 0 && S(OffCollate) == 1;
        return (land, paper, duplex, mono, copies, collate);
    }

    /// <summary>DEVMODE를 적용한 프린터 DC. 실패하면 IntPtr.Zero. 만든 스레드와 다른 스레드에서 쓰는 것이 설계다(PrintJob).</summary>
    public static IntPtr CreateDc(string printer, byte[]? devMode) => CreateDcWithMode("WINSPOOL", printer, null, devMode);

    /// <summary>DEVMODE를 적용한 DC의 장치 크기(인쇄 가능 영역 픽셀, 해상도, 용지 전체 픽셀·여백). 미리보기에서 쓴다.</summary>
    public static PrintSurface? Measure(string printer, byte[]? devMode)
    {
        IntPtr dc = CreateDc(printer, devMode);
        if (dc == IntPtr.Zero) return null;
        try
        {
            const int PHYSICALWIDTH = 110, PHYSICALHEIGHT = 111, PHYSICALOFFSETX = 112, PHYSICALOFFSETY = 113;
            return new PrintSurface(
                PrintInterop.Caps(dc, PrintInterop.HORZRES), PrintInterop.Caps(dc, PrintInterop.VERTRES),
                PrintInterop.Caps(dc, PrintInterop.LOGPIXELSX), PrintInterop.Caps(dc, PrintInterop.LOGPIXELSY),
                PrintInterop.Caps(dc, PHYSICALWIDTH), PrintInterop.Caps(dc, PHYSICALHEIGHT),
                PrintInterop.Caps(dc, PHYSICALOFFSETX), PrintInterop.Caps(dc, PHYSICALOFFSETY));
        }
        finally { PrintInterop.DeleteDc(dc); }
    }
}

/// <summary>프린터 DC의 장치 크기(모두 장치 픽셀). Width/Height = 인쇄 가능 영역, Physical* = 용지 전체, Offset* = 용지 왼쪽 위에서 인쇄 가능 영역까지.</summary>
public sealed record PrintSurface(int Width, int Height, int DpiX, int DpiY, int PhysicalWidth, int PhysicalHeight, int OffsetX, int OffsetY);
