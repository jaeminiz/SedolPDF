using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document.Saving;

namespace PdfEditor.App.Tests.Documents;

public class ErrorMessagesTests
{
    [Fact]
    public void ErrorMessages_MapsCodesAndStages()
    {
        Assert.Equal(Strings.Err_Pdfium_3, ErrorMessages.For(new PdfiumException(3, "dev")));
        Assert.Equal(Strings.Err_Pdfium_1, ErrorMessages.For(new PdfiumException(1, "dev")));
        Assert.Equal(Strings.Err_Pdfium_6, ErrorMessages.For(new PdfiumException(6, "dev")));
        Assert.Equal(string.Format(Strings.Err_Pdfium_Unknown, 42), ErrorMessages.For(new PdfiumException(42, "dev")));
        Assert.Equal(Strings.Err_PasswordWrong, ErrorMessages.For(new PdfPasswordRequiredException("op")));

        Assert.Equal(string.Format(Strings.Err_Save_ContentRegeneration, 3), ErrorMessages.For(new PdfEditor.Document.Output.ContentRegenerationException(3)));
        Assert.Equal(string.Format(Strings.Err_Save_ContentRegeneration, 3), ErrorMessages.For(new SaveException(SaveStage.PostProcess, new PdfEditor.Document.Output.ContentRegenerationException(3))));
        Assert.Equal(Strings.Err_Save_ReplaceLocked,
            ErrorMessages.For(new SaveException(SaveStage.Replace, new IOException("locked"))));
        Assert.Equal(Strings.Err_Save_Replace,
            ErrorMessages.For(new SaveException(SaveStage.Replace, new InvalidOperationException())));
        Assert.Equal(Strings.Err_Save_Write, ErrorMessages.For(new SaveException(SaveStage.Write, new IOException())));
        Assert.Equal(Strings.Err_Save_PostProcess, ErrorMessages.For(new SaveException(SaveStage.PostProcess, new IOException())));
        Assert.Equal(Strings.Err_Save_Verify, ErrorMessages.For(new SaveException(SaveStage.Verify, new IOException())));

        Assert.Equal(Strings.Err_AccessDenied, ErrorMessages.For(new UnauthorizedAccessException()));
        Assert.Equal(Strings.Err_Unexpected, ErrorMessages.For(new InvalidOperationException("x")));
    }

    [Fact]
    public void ErrorMessages_NeverLeaksDeveloperMessage()
    {
        Assert.DoesNotContain("dev-secret", ErrorMessages.For(new PdfiumException(3, "dev-secret")));
        Assert.DoesNotContain("dev-secret", ErrorMessages.For(new InvalidOperationException("dev-secret")));
    }
}
