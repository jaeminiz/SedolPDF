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
        PdfEditor.Document.Output.ContentRegenerationException c => ForRegeneration(c),
        SaveException { InnerException: PdfEditor.Document.Output.ContentRegenerationException c } => ForRegeneration(c),
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
        PdfEditor.Optimize.ReduceVerificationException => Strings.Err_ReduceVerify,
        UnauthorizedAccessException => Strings.Err_AccessDenied,
        FileNotFoundException or DirectoryNotFoundException => Strings.Err_Pdfium_2,
        _ => Strings.Err_Unexpected,
    };

    /// <summary>0.15.0: 모양이 달라질 수 있는 저장(LookChanged)은 모양 안내, 고쳐 쓸 수 없는 편집(CannotRewrite)은 원인을 쉬운 말로 한 번 안내한다.</summary>
    private static string ForRegeneration(PdfEditor.Document.Output.ContentRegenerationException c) =>
        c.Kind == PdfEditor.Document.Output.RegenerationFailure.LookChanged
            ? string.Format(Strings.Err_Save_ContentRegeneration, c.PageNumber)
            : string.Format(Strings.Err_Save_ObjectEditUnsupported, c.PageNumber, ReasonPhrase(c.Reason));

    /// <summary>개발자용 영어 사유(error.log에는 그대로 남는다)에서 열쇠말을 찾아 사용자 문구의 짧은 구절로 바꾼다.</summary>
    internal static string ReasonPhrase(string? reason)
    {
        string r = reason ?? "";
        if (r.Contains("text not copyable") || r.Contains("text not movable") || r.Contains("render mode")) return Strings.Err_Save_Reason_Text;
        if (r.Contains("clip")) return Strings.Err_Save_Reason_Clip;
        if (r.Contains("parse") || r.Contains("segment") || r.Contains("object count") || r.Contains("map")) return Strings.Err_Save_Reason_Structure;
        return Strings.Err_Save_Reason_Other;
    }
}
