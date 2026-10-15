using System.Runtime.InteropServices;

namespace Pdf.Native.Interop;

/// <summary>문서 정보·권한·손상 감지용 PDFium 함수. 호출은 PdfiumThread 안에서만 한다.</summary>
internal static unsafe partial class NativeMethods
{
    // Windows에서 unsigned long은 32비트이므로 uint로 받는다.
    [LibraryImport(Lib)] public static partial uint FPDF_GetDocPermissions(IntPtr doc);
    [LibraryImport(Lib)] public static partial int FPDF_GetSecurityHandlerRevision(IntPtr doc);
    [LibraryImport(Lib)] public static partial int FPDF_DocumentHasValidCrossReferenceTable(IntPtr doc);
    [LibraryImport(Lib)] public static partial int FPDF_GetFileVersion(IntPtr doc, int* fileVersion);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial uint FPDF_GetMetaText(IntPtr doc, string tag, IntPtr buffer, uint buflen);

    // 책갈피 (핸들은 PDFium이 소유하며 닫지 않는다)
    [LibraryImport(Lib)] public static partial IntPtr FPDFBookmark_GetFirstChild(IntPtr doc, IntPtr bookmark);
    [LibraryImport(Lib)] public static partial IntPtr FPDFBookmark_GetNextSibling(IntPtr doc, IntPtr bookmark);
    [LibraryImport(Lib)] public static partial uint FPDFBookmark_GetTitle(IntPtr bookmark, IntPtr buffer, uint buflen);
    [LibraryImport(Lib)] public static partial int FPDFBookmark_GetCount(IntPtr bookmark);
    [LibraryImport(Lib)] public static partial IntPtr FPDFBookmark_GetDest(IntPtr doc, IntPtr bookmark);
    [LibraryImport(Lib)] public static partial IntPtr FPDFBookmark_GetAction(IntPtr bookmark);
    [LibraryImport(Lib)] public static partial IntPtr FPDFAction_GetDest(IntPtr doc, IntPtr action);
    [LibraryImport(Lib)] public static partial int FPDFDest_GetDestPageIndex(IntPtr doc, IntPtr dest);

    // 첨부 파일
    [LibraryImport(Lib)] public static partial int FPDFDoc_GetAttachmentCount(IntPtr doc);
    [LibraryImport(Lib)] public static partial IntPtr FPDFDoc_GetAttachment(IntPtr doc, int index);
    [LibraryImport(Lib)] public static partial uint FPDFAttachment_GetName(IntPtr attachment, IntPtr buffer, uint buflen);
    [LibraryImport(Lib)] public static partial int FPDFAttachment_GetFile(IntPtr attachment, IntPtr buffer, uint buflen, uint* outBuflen);
}
