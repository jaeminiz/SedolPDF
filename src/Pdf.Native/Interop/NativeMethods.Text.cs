using System.Runtime.InteropServices;

namespace Pdf.Native.Interop;

/// <summary>텍스트 검색·선택용 PDFium 함수. 호출은 PdfiumThread 안에서만 한다.</summary>
internal static unsafe partial class NativeMethods
{
    public const ulong FPDF_MATCHCASE = 1;
    public const ulong FPDF_MATCHWHOLEWORD = 2;

    [LibraryImport(Lib)] public static partial IntPtr FPDFText_LoadPage(IntPtr page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(IntPtr textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_CountChars(IntPtr textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_GetText(IntPtr textPage, int startIndex, int count, ushort* result);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharIndexAtPos(IntPtr textPage, double x, double y, double xTolerance, double yTolerance);
    [LibraryImport(Lib)] public static partial uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharBox(IntPtr textPage, int index, double* left, double* right, double* bottom, double* top);
    [LibraryImport(Lib)] public static partial int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);
    [LibraryImport(Lib)] public static partial int FPDFText_GetRect(IntPtr textPage, int rectIndex, double* left, double* top, double* right, double* bottom);

    [LibraryImport(Lib)] public static partial IntPtr FPDFText_FindStart(IntPtr textPage, ushort* findWhat, ulong flags, int startIndex);
    [LibraryImport(Lib)] public static partial int FPDFText_FindNext(IntPtr searchHandle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchResultIndex(IntPtr searchHandle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchCount(IntPtr searchHandle);
    [LibraryImport(Lib)] public static partial void FPDFText_FindClose(IntPtr searchHandle);

    /// <summary>테스트 전용: PageTransform 검증용.</summary>
    [LibraryImport(Lib)]
    public static partial int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate,
        double pageX, double pageY, int* deviceX, int* deviceY);
}
