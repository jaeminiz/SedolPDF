using System.Runtime.InteropServices;

namespace Pdf.Native.Interop;

/// <summary>페이지 객체(fpdf_edit.h) 함수. 호출은 PdfiumThread 안에서만 한다 (M4).</summary>
internal static unsafe partial class NativeMethods
{
    public const int FPDF_PAGEOBJ_TEXT = 1, FPDF_PAGEOBJ_PATH = 2, FPDF_PAGEOBJ_IMAGE = 3, FPDF_PAGEOBJ_SHADING = 4, FPDF_PAGEOBJ_FORM = 5;
    public const int FPDF_FILLMODE_NONE = 0, FPDF_FILLMODE_ALTERNATE = 1, FPDF_FILLMODE_WINDING = 2;

    [LibraryImport(Lib)] public static partial int FPDFPage_CountObjects(IntPtr page);
    [LibraryImport(Lib)] public static partial IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [LibraryImport(Lib)] public static partial int FPDFPage_InsertObjectAtIndex(IntPtr page, IntPtr obj, nuint index);
    [LibraryImport(Lib)] public static partial int FPDFPage_GenerateContent(IntPtr page);
    [LibraryImport(Lib)] public static partial void FPDFPageObj_Destroy(IntPtr obj);

    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetType(IntPtr obj);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetBounds(IntPtr obj, float* left, float* bottom, float* right, float* top);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetRotatedBounds(IntPtr obj, float* quad8);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetMatrix(IntPtr obj, FsMatrix* matrix);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetMatrix(IntPtr obj, FsMatrix* matrix);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetIsActive(IntPtr obj, int active);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetIsActive(IntPtr obj, int* active);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetFillColor(IntPtr obj, uint* r, uint* g, uint* b, uint* a);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetFillColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetStrokeColor(IntPtr obj, uint* r, uint* g, uint* b, uint* a);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetStrokeColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetStrokeWidth(IntPtr obj, float* width);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetStrokeWidth(IntPtr obj, float width);

    [LibraryImport(Lib)] public static partial IntPtr FPDFTextObj_GetFont(IntPtr textObj);
    [LibraryImport(Lib)] public static partial int FPDFTextObj_GetFontSize(IntPtr textObj, float* size);
    [LibraryImport(Lib)] public static partial int FPDFTextObj_GetTextRenderMode(IntPtr textObj);
    [LibraryImport(Lib)] public static partial int FPDFTextObj_SetTextRenderMode(IntPtr textObj, int mode);
    [LibraryImport(Lib)] public static partial nuint FPDFTextObj_GetText(IntPtr textObj, IntPtr textPage, ushort* buffer, nuint length);
    [LibraryImport(Lib)] public static partial IntPtr FPDFPageObj_CreateTextObj(IntPtr doc, IntPtr font, float size);
    [LibraryImport(Lib)] public static partial int FPDFText_SetText(IntPtr textObj, ushort* text);
    [LibraryImport(Lib)] public static partial nuint FPDFFont_GetBaseFontName(IntPtr font, byte* buffer, nuint length);
    [LibraryImport(Lib)] public static partial int FPDFFont_GetIsEmbedded(IntPtr font);

    [LibraryImport(Lib)] public static partial int FPDFFormObj_CountObjects(IntPtr form);
    [LibraryImport(Lib)] public static partial IntPtr FPDFFormObj_GetObject(IntPtr form, nuint index);

    [LibraryImport(Lib)] public static partial IntPtr FPDFPageObj_NewImageObj(IntPtr doc);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_SetBitmap(IntPtr* pages, int count, IntPtr imageObj, IntPtr bitmap);

    [LibraryImport(Lib)] public static partial IntPtr FPDFPageObj_CreateNewPath(float x, float y);
    [LibraryImport(Lib)] public static partial int FPDFPath_MoveTo(IntPtr path, float x, float y);
    [LibraryImport(Lib)] public static partial int FPDFPath_LineTo(IntPtr path, float x, float y);
    [LibraryImport(Lib)] public static partial int FPDFPath_BezierTo(IntPtr path, float x1, float y1, float x2, float y2, float x3, float y3);
    [LibraryImport(Lib)] public static partial int FPDFPath_Close(IntPtr path);
    [LibraryImport(Lib)] public static partial int FPDFPath_SetDrawMode(IntPtr path, int fillMode, int stroke);
    [LibraryImport(Lib)] public static partial int FPDFPath_GetDrawMode(IntPtr path, int* fillMode, int* stroke);
    [LibraryImport(Lib)] public static partial int FPDFPath_CountSegments(IntPtr path);
    [LibraryImport(Lib)] public static partial IntPtr FPDFPath_GetPathSegment(IntPtr path, int index);
    [LibraryImport(Lib)] public static partial int FPDFPathSegment_GetPoint(IntPtr segment, float* x, float* y);
    [LibraryImport(Lib)] public static partial int FPDFPathSegment_GetType(IntPtr segment);
    [LibraryImport(Lib)] public static partial int FPDFPathSegment_GetClose(IntPtr segment);
}

/// <summary>FPDF_FILEACCESS (Windows: unsigned long = 32비트).</summary>
internal unsafe struct FpdfFileAccess
{
    public uint FileLen;
    public delegate* unmanaged[Cdecl]<void*, uint, byte*, uint, int> GetBlock;
    public void* Param;
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(Lib)] public static partial int FPDFImageObj_LoadJpegFileInline(IntPtr* pages, int count, IntPtr imageObj, FpdfFileAccess* fileAccess);
}
