using System.Runtime.InteropServices;

namespace Pdf.Native.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct FsSizeF
{
    public float Width, Height;
}

/// <summary>페이지 편집·주석 열거용 PDFium 함수. 호출은 PdfiumThread 안에서만 한다.</summary>
internal static unsafe partial class NativeMethods
{
    public const int FPDFANNOT_FLAG_HIDDEN = 2;

    [LibraryImport(Lib)] public static partial int FPDF_ImportPagesByIndex(IntPtr destDoc, IntPtr srcDoc, int* pageIndices, ulong length, int index);
    [LibraryImport(Lib)] public static partial void FPDFPage_Delete(IntPtr doc, int pageIndex);
    [LibraryImport(Lib)] public static partial int FPDF_MovePages(IntPtr doc, int* pageIndices, ulong pageIndicesLen, int destPageIndex);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageSizeByIndexF(IntPtr doc, int pageIndex, FsSizeF* size);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetRotation(IntPtr page);
    [LibraryImport(Lib)] public static partial void FPDFPage_SetRotation(IntPtr page, int rotate);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageBoundingBox(IntPtr page, FsRectF* rect);

    [LibraryImport(Lib)] public static partial int FPDFPage_GetAnnotCount(IntPtr page);
    [LibraryImport(Lib)] public static partial IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [LibraryImport(Lib)] public static partial void FPDFPage_CloseAnnot(IntPtr annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetSubtype(IntPtr annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetFlags(IntPtr annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetFlags(IntPtr annot, int flags);
}
