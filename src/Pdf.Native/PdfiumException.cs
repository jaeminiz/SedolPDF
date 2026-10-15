namespace Pdf.Native;

/// <summary>PDFium 오류 코드(FPDF_GetLastError)를 담는 예외.</summary>
public class PdfiumException : Exception
{
    public uint ErrorCode { get; }

    public PdfiumException(uint errorCode, string message) : base(message) => ErrorCode = errorCode;

    internal static PdfiumException FromLastError(string operation)
    {
        uint code = Interop.NativeMethods.FPDF_GetLastError();
        return code == Interop.NativeMethods.FPDF_ERR_PASSWORD
            ? new PdfPasswordRequiredException(operation)
            : new PdfiumException(code, $"{operation} 실패: {Describe(code)} (코드 {code})");
    }

    private static string Describe(uint code) => code switch
    {
        1 => "알 수 없는 오류",
        2 => "파일을 찾거나 열 수 없음",
        3 => "PDF 형식이 올바르지 않음",
        4 => "암호가 필요하거나 암호가 틀림",
        5 => "지원하지 않는 보안 처리기",
        6 => "페이지를 찾을 수 없거나 내용이 올바르지 않음",
        _ => "오류",
    };
}

/// <summary>암호가 필요한 PDF를 암호 없이(또는 틀린 암호로) 열었을 때.</summary>
public sealed class PdfPasswordRequiredException : PdfiumException
{
    public PdfPasswordRequiredException(string operation)
        : base(Interop.NativeMethods.FPDF_ERR_PASSWORD, $"{operation} 실패: 암호가 필요하거나 암호가 틀립니다.") { }
}
