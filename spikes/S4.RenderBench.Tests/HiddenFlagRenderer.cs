using System.Runtime.InteropServices;
using Pdf.Native;
using Pdf.Native.Interop;

namespace S4.RenderBench.Tests;

internal static class FpdfAnnotSubtype
{
    public const int Square = 5;
    public const int Squiggly = 11;
}

internal static unsafe partial class AnnotNative
{
    private const string Lib = "pdfium";
    [LibraryImport(Lib)] public static partial IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetAnnotCount(IntPtr page);
    [LibraryImport(Lib)] public static partial IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [LibraryImport(Lib)] public static partial void FPDFPage_CloseAnnot(IntPtr annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetSubtype(IntPtr annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetFlags(IntPtr annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetFlags(IntPtr annot, int flags);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetRect(IntPtr annot, FsRectF* rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetColor(IntPtr annot, int colorType, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetBorder(IntPtr annot, float horizontalRadius, float verticalRadius, float borderWidth);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, float* quad8);
}

/// <summary>검증용 샘플: 빨강 Square(지원 유형)와 파랑 Squiggly(우리 모델이 지원하지 않는 유형)를 만든다.</summary>
internal static unsafe class SampleAnnots
{
    public static void AddSquareAndSquiggly(PdfDocument doc, int pageIndex)
    {
        using var page = doc.LoadPage(pageIndex);

        var sq = AnnotNative.FPDFPage_CreateAnnot(page.Handle, FpdfAnnotSubtype.Square);
        var r1 = new FsRectF { Left = 100, Bottom = 600, Right = 300, Top = 650 };
        AnnotNative.FPDFAnnot_SetRect(sq, &r1);
        AnnotNative.FPDFAnnot_SetColor(sq, 0, 255, 0, 0, 255);
        AnnotNative.FPDFAnnot_SetBorder(sq, 0, 0, 4);
        AnnotNative.FPDFPage_CloseAnnot(sq);

        var sg = AnnotNative.FPDFPage_CreateAnnot(page.Handle, FpdfAnnotSubtype.Squiggly);
        var r2 = new FsRectF { Left = 100, Bottom = 500, Right = 300, Top = 540 };
        AnnotNative.FPDFAnnot_SetRect(sg, &r2);
        AnnotNative.FPDFAnnot_SetColor(sg, 0, 0, 0, 255, 255);
        float* quad = stackalloc float[8] { 100, 540, 300, 540, 100, 500, 300, 500 };
        AnnotNative.FPDFAnnot_AppendAttachmentPoints(sg, quad);
        AnnotNative.FPDFPage_CloseAnnot(sg);
    }

    public static int[] GetFlags(PdfPage page)
    {
        int n = AnnotNative.FPDFPage_GetAnnotCount(page.Handle);
        var flags = new int[n];
        for (int i = 0; i < n; i++)
        {
            var a = AnnotNative.FPDFPage_GetAnnot(page.Handle, i);
            flags[i] = AnnotNative.FPDFAnnot_GetFlags(a);
            AnnotNative.FPDFPage_CloseAnnot(a);
        }
        return flags;
    }
}

/// <summary>설계서 4.2-4: 렌더링하는 동안에만 지원 유형 주석에 Hidden(2) 플래그를 붙여 나머지 주석만 그린다.</summary>
internal static class HiddenFlagRenderer
{
    private const int Hidden = 2;

    /// <returns>렌더 뒤 모든 주석의 플래그 (복원 확인용)</returns>
    public static int[] RenderWithSupportedHidden(PdfPage page, int w, int h, int hideSubtype, out byte[] bgra)
    {
        int n = AnnotNative.FPDFPage_GetAnnotCount(page.Handle);
        var changed = new List<(int index, int original)>();
        try
        {
            for (int i = 0; i < n; i++)
            {
                var a = AnnotNative.FPDFPage_GetAnnot(page.Handle, i);
                if (AnnotNative.FPDFAnnot_GetSubtype(a) == hideSubtype)
                {
                    int f = AnnotNative.FPDFAnnot_GetFlags(a);
                    changed.Add((i, f));
                    AnnotNative.FPDFAnnot_SetFlags(a, f | Hidden);
                }
                AnnotNative.FPDFPage_CloseAnnot(a);
            }
            bgra = page.RenderTile(1.0, 0, 0, w, h, withAnnotations: true);
        }
        finally
        {
            foreach (var (index, original) in changed)
            {
                var a = AnnotNative.FPDFPage_GetAnnot(page.Handle, index);
                AnnotNative.FPDFAnnot_SetFlags(a, original);
                AnnotNative.FPDFPage_CloseAnnot(a);
            }
        }
        return SampleAnnots.GetFlags(page);
    }
}
