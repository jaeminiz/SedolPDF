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
}
