using System.Runtime.InteropServices;

namespace PdfEditor.App.Printing;

public sealed class PrintException(string message, int win32Error = 0) : Exception(message)
{
    public int Win32Error { get; } = win32Error;
}

/// <summary>PrintDlgExW 결과. Dc는 호출자가 DeleteDc로 지운다.</summary>
public sealed record PrintDialogResult(IntPtr Dc, IReadOnlyList<int> PageIndices);

/// <summary>인쇄에 쓰는 Win32 호출(gdi32, comdlg32) 얇은 래퍼.</summary>
public static partial class PrintInterop
{
    public const int HORZRES = 8, VERTRES = 10, LOGPIXELSX = 88, LOGPIXELSY = 90;

    [StructLayout(LayoutKind.Sequential)]
    private struct DOCINFOW
    {
        public int cbSize;
        public IntPtr lpszDocName;
        public IntPtr lpszOutput;
        public IntPtr lpszDatatype;
        public uint fwType;
    }

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDCW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateDCW(string? driver, string? device, string? output, IntPtr devMode);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int StartDocW(IntPtr hdc, ref DOCINFOW di);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int StartPage(IntPtr hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int EndPage(IntPtr hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int EndDoc(IntPtr hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int AbortDoc(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDeviceCaps(IntPtr hdc, int index);

    /// <summary>프린터 DC를 만든다("WINSPOOL"). 실패하면 IntPtr.Zero.</summary>
    public static IntPtr CreateDc(string printerName) => CreateDCW("WINSPOOL", printerName, null, IntPtr.Zero);

    public static void DeleteDc(IntPtr hdc) { if (hdc != IntPtr.Zero) DeleteDC(hdc); }

    /// <summary>작업 번호(양수), 실패하면 0 이하. outputFile을 주면 프린터가 그 파일에 직접 쓴다(Microsoft Print to PDF 등).</summary>
    public static int StartDoc(IntPtr hdc, string docName, string? outputFile)
    {
        IntPtr name = Marshal.StringToHGlobalUni(docName);
        IntPtr output = outputFile is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(outputFile);
        try
        {
            var di = new DOCINFOW { cbSize = Marshal.SizeOf<DOCINFOW>(), lpszDocName = name, lpszOutput = output };
            return StartDocW(hdc, ref di);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            if (output != IntPtr.Zero) Marshal.FreeHGlobal(output);
        }
    }

    public static bool StartPageOk(IntPtr hdc) => StartPage(hdc) > 0;
    public static bool EndPageOk(IntPtr hdc) => EndPage(hdc) > 0;
    public static bool EndDocOk(IntPtr hdc) => EndDoc(hdc) > 0;
    public static void AbortDocQuiet(IntPtr hdc) => AbortDoc(hdc);
    public static int Caps(IntPtr hdc, int index) => GetDeviceCaps(hdc, index);

    // ---- 공용 인쇄 대화상자 (PrintDlgExW) ----

    private const uint PD_ALLPAGES = 0, PD_PAGENUMS = 0x2, PD_NOSELECTION = 0x4, PD_RETURNDC = 0x100,
        PD_USEDEVMODECOPIESANDCOLLATE = 0x40000, PD_CURRENTPAGE = 0x400000;
    private const uint START_PAGE_GENERAL = 0xFFFFFFFF;
    private const uint PD_RESULT_PRINT = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTPAGERANGE { public uint nFromPage, nToPage; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTDLGEXW
    {
        public uint lStructSize;
        public IntPtr hwndOwner, hDevMode, hDevNames, hDC;
        public uint Flags, Flags2, ExclusionFlags, nPageRanges, nMaxPageRanges;
        public IntPtr lpPageRanges;
        public uint nMinPage, nMaxPage, nCopies;
        public IntPtr hInstance, lpPrintTemplateName, lpCallback;
        public uint nPropertyPages;
        public IntPtr lphPropertyPages;
        public uint nStartPage, dwResultAction;
    }

    [LibraryImport("comdlg32.dll")]
    private static partial int PrintDlgExW(ref PRINTDLGEXW pd);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalFree(IntPtr h);

    /// <summary>
    /// Win32 인쇄 대화상자를 모달로 띄운다(UI 스레드에서 부르며, 대화상자가 자체 메시지 루프를 돈다). 취소하면 null.
    /// 복사 매수·한 부씩은 프린터 드라이버(DEVMODE)가 처리하므로 한 번만 인쇄하면 된다.
    /// 검증 필요: 실제 대화상자·실제 프린터 동작은 자동 테스트가 없다(수동 확인).
    /// </summary>
    public static PrintDialogResult? ShowPrintDialog(IntPtr owner, int pageCount, int currentPageIndex)
    {
        const int maxRanges = 32;
        IntPtr ranges = Marshal.AllocHGlobal(Marshal.SizeOf<PRINTPAGERANGE>() * maxRanges);
        try
        {
            var pd = new PRINTDLGEXW
            {
                lStructSize = (uint)Marshal.SizeOf<PRINTDLGEXW>(),
                hwndOwner = owner,
                Flags = PD_ALLPAGES | PD_RETURNDC | PD_USEDEVMODECOPIESANDCOLLATE | PD_NOSELECTION,
                nPageRanges = 0,
                nMaxPageRanges = maxRanges,
                lpPageRanges = ranges,
                nMinPage = 1,
                nMaxPage = (uint)pageCount,
                nCopies = 1,
                nStartPage = START_PAGE_GENERAL,
            };
            int hr = PrintDlgExW(ref pd);
            try
            {
                if (hr != 0) throw new PrintException("PrintDlgExW failed", hr);
                if (pd.dwResultAction != PD_RESULT_PRINT || pd.hDC == IntPtr.Zero)
                {
                    DeleteDc(pd.hDC);
                    return null;
                }
                var pages = new List<int>();
                if ((pd.Flags & PD_CURRENTPAGE) != 0)
                    pages.Add(Math.Clamp(currentPageIndex, 0, pageCount - 1));
                else if ((pd.Flags & PD_PAGENUMS) != 0)
                {
                    int size = Marshal.SizeOf<PRINTPAGERANGE>();
                    for (int i = 0; i < pd.nPageRanges; i++)
                    {
                        var r = Marshal.PtrToStructure<PRINTPAGERANGE>(ranges + i * size);
                        int from = (int)Math.Min(r.nFromPage, r.nToPage), to = (int)Math.Max(r.nFromPage, r.nToPage);
                        for (int p = Math.Max(1, from); p <= Math.Min(pageCount, to); p++)
                            if (!pages.Contains(p - 1)) pages.Add(p - 1);
                    }
                }
                else
                    for (int i = 0; i < pageCount; i++) pages.Add(i);
                return new PrintDialogResult(pd.hDC, pages);
            }
            finally
            {
                if (pd.hDevMode != IntPtr.Zero) GlobalFree(pd.hDevMode);
                if (pd.hDevNames != IntPtr.Zero) GlobalFree(pd.hDevNames);
            }
        }
        finally { Marshal.FreeHGlobal(ranges); }
    }
}
