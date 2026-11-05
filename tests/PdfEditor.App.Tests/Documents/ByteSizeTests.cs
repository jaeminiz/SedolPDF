using PdfEditor.App.Documents;

namespace PdfEditor.App.Tests.Documents;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2 KB")]
    [InlineData(1536L * 1024, "1.5 MB")]
    [InlineData(16L * 1024 * 1024, "16 MB")]
    [InlineData(52_300_000L, "49.9 MB")]
    public void Format_UsesBinaryUnitsWithOneDecimalAtMost(long bytes, string expected)
        => Assert.Equal(expected, ByteSize.Format(bytes));

    [Theory]
    [InlineData("16", 16L * 1024 * 1024)]
    [InlineData(" 8 ", 8L * 1024 * 1024)]
    [InlineData("1.5", 1572864L)]
    [InlineData("1,5", 1572864L)]
    [InlineData("2000", 2000L * 1024 * 1024)]
    [InlineData("1", 1024L * 1024)]
    public void TryParseMegabytes_AcceptsNumbersFromOneToTwoThousand(string text, long expected)
    {
        Assert.True(ByteSize.TryParseMegabytes(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("2001")]
    [InlineData("-3")]
    [InlineData("1e3")]
    public void TryParseMegabytes_RejectsEverythingElse(string? text)
        => Assert.False(ByteSize.TryParseMegabytes(text, out _));
}
