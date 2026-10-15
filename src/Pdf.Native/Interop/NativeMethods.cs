using System.Runtime.InteropServices;

namespace Pdf.Native.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct FpdfLibraryConfig
{
    public int Version;
    public IntPtr UserFontPaths;
    public IntPtr Isolate;
    public uint V8EmbedderSlot;
    public IntPtr Platform;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FsMatrix
{
    public float A, B, C, D, E, F;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FsRectF
{
    public float Left, Top, Right, Bottom;
}

/// <summary>FPDF_FILEWRITE. 뒤에 붙은 Handle은 우리가 쓰는 GCHandle(FileStream).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FpdfFileWrite
{
    public int Version;
    public delegate* unmanaged[Cdecl]<FpdfFileWrite*, void*, nuint, int> WriteBlock;
    public IntPtr Handle;
}

/// <summary>필요한 PDFium 함수만 선언한다. 호출은 PdfiumThread 안에서만 한다.</summary>
internal static unsafe partial class NativeMethods
{
    public const uint FPDF_ERR_PASSWORD = 4;
    public const int FPDFBitmap_BGRA = 4;
    public const int FPDF_ANNOT = 0x01;

    private const string Lib = "pdfium";

    [LibraryImport(Lib)] public static partial void FPDF_InitLibraryWithConfig(FpdfLibraryConfig* config);
    [LibraryImport(Lib)] public static partial void FPDF_DestroyLibrary();
    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();

    [LibraryImport(Lib)] public static partial IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, byte* password);
    [LibraryImport(Lib)] public static partial IntPtr FPDF_CreateNewDocument();
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(IntPtr doc);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(IntPtr doc);

    [LibraryImport(Lib)] public static partial IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(IntPtr page);
    [LibraryImport(Lib)] public static partial IntPtr FPDFPage_New(IntPtr doc, int index, double width, double height);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageWidthF(IntPtr page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageHeightF(IntPtr page);

    [LibraryImport(Lib)] public static partial int FPDF_SaveAsCopy(IntPtr doc, FpdfFileWrite* writer, uint flags);

    [LibraryImport(Lib)] public static partial IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(IntPtr bitmap);
    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmapWithMatrix(IntPtr bitmap, IntPtr page, FsMatrix* matrix, FsRectF* clipping, int flags);
}
