using System.Runtime.InteropServices;

namespace Pdf.Native.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct FsPointF
{
    public float X, Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FsQuadPointsF
{
    public float X1, Y1, X2, Y2, X3, Y3, X4, Y4;
}

/// <summary>주석 생성·읽기·쓰기용 PDFium 함수 (fpdf_annot.h). 호출은 PdfiumThread 안에서만 한다.</summary>
internal static unsafe partial class NativeMethods
{
    public const int FPDF_ANNOT_APPEARANCEMODE_NORMAL = 0;

    [LibraryImport(Lib)] public static partial IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [LibraryImport(Lib)] public static partial int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetAnnotIndex(IntPtr page, IntPtr annot);

    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetRect(IntPtr annot, FsRectF* rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetRect(IntPtr annot, FsRectF* rect);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int FPDFAnnot_HasKey(IntPtr annot, string key);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial uint FPDFAnnot_GetStringValue(IntPtr annot, string key, IntPtr buffer, uint buflen);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int FPDFAnnot_SetStringValue(IntPtr annot, string key, char* value);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int FPDFAnnot_GetNumberValue(IntPtr annot, string key, float* value);

    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetColor(IntPtr annot, int colorType, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetBorder(IntPtr annot, float horizontalRadius, float verticalRadius, float borderWidth);

    [LibraryImport(Lib)] public static partial nuint FPDFAnnot_CountAttachmentPoints(IntPtr annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetAttachmentPoints(IntPtr annot, nuint quadIndex, FsQuadPointsF* quad);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetAttachmentPoints(IntPtr annot, nuint quadIndex, FsQuadPointsF* quad);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, FsQuadPointsF* quad);

    [LibraryImport(Lib)] public static partial nuint FPDFAnnot_GetInkListCount(IntPtr annot);
    [LibraryImport(Lib)] public static partial uint FPDFAnnot_GetInkListPath(IntPtr annot, uint pathIndex, FsPointF* buffer, uint length);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_AddInkStroke(IntPtr annot, FsPointF* points, nuint pointCount);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_RemoveInkList(IntPtr annot);

    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetAP(IntPtr annot, int appearanceMode, char* value);
    [LibraryImport(Lib)] public static partial uint FPDFAnnot_GetAP(IntPtr annot, int appearanceMode, IntPtr buffer, uint buflen);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr FPDFAnnot_GetLinkedAnnot(IntPtr annot, string key);
}
