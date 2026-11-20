using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document.Saving;

namespace PdfEditor.App.Tests.Documents;

public class ErrorMessagesTests
{
    [Fact]
    public void ErrorMessages_ReduceVerificationFailure_HasItsOwnMessage()
    {
        Assert.Equal(Strings.Err_ReduceVerify, ErrorMessages.For(new PdfEditor.Optimize.ReduceVerificationException()));
        Assert.NotEqual(Strings.Err_Unexpected, Strings.Err_ReduceVerify);
    }

    [Fact]
    public void ErrorMessages_MapsCodesAndStages()
    {
        Assert.Equal(Strings.Err_Pdfium_3, ErrorMessages.For(new PdfiumException(3, "dev")));
        Assert.Equal(Strings.Err_Pdfium_1, ErrorMessages.For(new PdfiumException(1, "dev")));
        Assert.Equal(Strings.Err_Pdfium_6, ErrorMessages.For(new PdfiumException(6, "dev")));
        Assert.Equal(string.Format(Strings.Err_Pdfium_Unknown, 42), ErrorMessages.For(new PdfiumException(42, "dev")));
        Assert.Equal(Strings.Err_PasswordWrong, ErrorMessages.For(new PdfPasswordRequiredException("op")));

        var lookKind = PdfEditor.Document.Output.RegenerationFailure.LookChanged;
        Assert.Equal(string.Format(Strings.Err_Save_ContentRegeneration, 3), ErrorMessages.For(new PdfEditor.Document.Output.ContentRegenerationException(3, null, lookKind)));
        Assert.Equal(string.Format(Strings.Err_Save_ContentRegeneration, 3), ErrorMessages.For(new SaveException(SaveStage.PostProcess, new PdfEditor.Document.Output.ContentRegenerationException(3, null, lookKind))));
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

    [Theory]
    [InlineData("object 2: text not copyable (position unknown or state changed inside BT)", "Text")]
    [InlineData("object 5: text not movable (position unknown or state changed inside BT)", "Text")]
    [InlineData("object 5: text render mode 7 clips", "Text")]
    [InlineData("object 4: clip cannot be rewritten", "Clip")]
    [InlineData("content not parsed: bad token", "Structure")]
    [InlineData("segmentation failed: bad cm at op 3", "Structure")]
    [InlineData("object count of Text is 20, expected 10", "Structure")]
    [InlineData("object map: counts differ", "Structure")]
    [InlineData("some other thing", "Other")]
    [InlineData(null, "Other")]
    public void ErrorMessages_CannotRewrite_ExplainsTheCauseOnce(string? reason, string which)
    {
        string phrase = which switch { "Text" => Strings.Err_Save_Reason_Text, "Clip" => Strings.Err_Save_Reason_Clip, "Structure" => Strings.Err_Save_Reason_Structure, _ => Strings.Err_Save_Reason_Other };
        var cannot = new PdfEditor.Document.Output.ContentRegenerationException(3, reason, PdfEditor.Document.Output.RegenerationFailure.CannotRewrite);
        Assert.Equal(string.Format(Strings.Err_Save_ObjectEditUnsupported, 3, phrase), ErrorMessages.For(cannot));
        Assert.Equal(string.Format(Strings.Err_Save_ObjectEditUnsupported, 3, phrase), ErrorMessages.For(new SaveException(SaveStage.PostProcess, cannot)));
    }

    [Fact]
    public void ErrorMessages_LookChanged_KeepsTheLookMessage()
    {
        var look = new PdfEditor.Document.Output.ContentRegenerationException(3, "x", PdfEditor.Document.Output.RegenerationFailure.LookChanged);
        Assert.Equal(string.Format(Strings.Err_Save_ContentRegeneration, 3), ErrorMessages.For(look));
        Assert.NotEqual(Strings.Err_Save_ObjectEditUnsupported, Strings.Err_Save_ContentRegeneration);
    }

    [Fact]
    public void ErrorMessages_NeverLeaksDeveloperMessage()
    {
        Assert.DoesNotContain("dev-secret", ErrorMessages.For(new PdfiumException(3, "dev-secret")));
        Assert.DoesNotContain("dev-secret", ErrorMessages.For(new InvalidOperationException("dev-secret")));
    }
}
