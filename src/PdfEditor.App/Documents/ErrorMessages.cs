using Pdf.Native;
using PdfEditor.App.Resources;
using PdfEditor.Document.Saving;

namespace PdfEditor.App.Documents;

/// <summary>예외를 사용자에게 보여 줄 한국어 문구로 바꾼다. 개발자용 예외 메시지는 쓰지 않는다 (설계서 8.2).</summary>
public static class ErrorMessages
{
    public static string For(Exception ex) => ex switch
    {
        PdfPasswordRequiredException => Strings.Err_PasswordWrong,
        PdfiumException p => p.ErrorCode switch
        {
            1 => Strings.Err_Pdfium_1,
            2 => Strings.Err_Pdfium_2,
            3 => Strings.Err_Pdfium_3,
            4 => Strings.Err_Pdfium_4,
            5 => Strings.Err_Pdfium_5,
            6 => Strings.Err_Pdfium_6,
            _ => string.Format(Strings.Err_Pdfium_Unknown, p.ErrorCode),
        },
        SaveException s => s.Stage switch
        {
            SaveStage.Write => Strings.Err_Save_Write,
            SaveStage.PostProcess => Strings.Err_Save_PostProcess,
            SaveStage.Verify => Strings.Err_Save_Verify,
            SaveStage.Replace when s.InnerException is IOException => Strings.Err_Save_ReplaceLocked,
            SaveStage.Replace => Strings.Err_Save_Replace,
            _ => Strings.Err_Unexpected,
        },
        PdfEditor.Exchange.Xfdf.XfdfFormatException { Failure: PdfEditor.Exchange.Xfdf.XfdfFailure.TooLarge } => Strings.Err_XfdfTooLarge,
        PdfEditor.Exchange.Xfdf.XfdfFormatException => Strings.Err_XfdfInvalid,
        UnauthorizedAccessException => Strings.Err_AccessDenied,
        FileNotFoundException or DirectoryNotFoundException => Strings.Err_Pdfium_2,
        _ => Strings.Err_Unexpected,
    };
}
