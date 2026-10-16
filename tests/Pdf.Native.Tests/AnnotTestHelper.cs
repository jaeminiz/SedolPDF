using System.Runtime.InteropServices;
using Pdf.Native.Interop;

namespace Pdf.Native.Tests;

internal static unsafe partial class AnnotNative
{
    private const string Lib = "pdfium";
    [LibraryImport(Lib)] public static partial IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetRect(IntPtr annot, FsRectF* rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetColor(IntPtr annot, int colorType, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetBorder(IntPtr annot, float horizontalRadius, float verticalRadius, float borderWidth);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, float* quad8);
}

/// <summary>테스트용 주석 생성·플래그 조회. PDFium 스레드 안에서만 호출한다.</summary>
internal static unsafe class AnnotTestHelper
{
    /// <summary>빨강 Square(5)와 파랑 Squiggly(11)를 만든다 (S4 샘플과 같은 값).</summary>
    public static void AddSquareAndSquiggly(PdfDocument doc, int pageIndex)
    {
        using var page = doc.LoadPage(pageIndex);

        var sq = AnnotNative.FPDFPage_CreateAnnot(page.Handle, PdfAnnotSubtype.Square);
        var r1 = new FsRectF { Left = 100, Bottom = 600, Right = 300, Top = 650 };
        AnnotNative.FPDFAnnot_SetRect(sq, &r1);
        AnnotNative.FPDFAnnot_SetColor(sq, 0, 255, 0, 0, 255);
        AnnotNative.FPDFAnnot_SetBorder(sq, 0, 0, 4);
        NativeMethods.FPDFPage_CloseAnnot(sq);

        var sg = AnnotNative.FPDFPage_CreateAnnot(page.Handle, PdfAnnotSubtype.Squiggly);
        var r2 = new FsRectF { Left = 100, Bottom = 500, Right = 300, Top = 540 };
        AnnotNative.FPDFAnnot_SetRect(sg, &r2);
        AnnotNative.FPDFAnnot_SetColor(sg, 0, 0, 0, 255, 255);
        float* quad = stackalloc float[8] { 100, 540, 300, 540, 100, 500, 300, 500 };
        AnnotNative.FPDFAnnot_AppendAttachmentPoints(sg, quad);
        NativeMethods.FPDFPage_CloseAnnot(sg);
    }

    public static int[] GetFlags(PdfPage page)
    {
        int n = NativeMethods.FPDFPage_GetAnnotCount(page.Handle);
        var flags = new int[n];
        for (int i = 0; i < n; i++)
        {
            var a = NativeMethods.FPDFPage_GetAnnot(page.Handle, i);
            flags[i] = NativeMethods.FPDFAnnot_GetFlags(a);
            NativeMethods.FPDFPage_CloseAnnot(a);
        }
        return flags;
    }
}
