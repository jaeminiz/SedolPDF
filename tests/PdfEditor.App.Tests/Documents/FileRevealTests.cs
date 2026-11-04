using PdfEditor.App.Documents;
using Xunit;

namespace PdfEditor.App.Tests.Documents;

public class FileRevealTests
{
    [Fact]
    public void BuildArguments_QuotesPathWithSpacesAndKorean()
    {
        string path = @"D:\INBOX WP\도면 검토\8372-8372416-0.PDF";
        Assert.Equal("/select,\"" + path + "\"", FileReveal.BuildArguments(path, _ => true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildArguments_NoPath_ReturnsNull(string? path) => Assert.Null(FileReveal.BuildArguments(path, _ => true));

    [Fact]
    public void BuildArguments_MissingFile_ReturnsNull() => Assert.Null(FileReveal.BuildArguments(@"D:\없는 폴더\x.pdf", _ => false));

    [Fact]
    public void BuildArguments_QuoteInPath_ReturnsNull() => Assert.Null(FileReveal.BuildArguments("D:\a\"b.pdf", _ => true));
}
