using PdfEditor.Pages;

namespace PdfEditor.Pages.Tests;

public class PageRangeParserTests
{
    [Theory]
    [InlineData("", 5, new[] { 0, 1, 2, 3, 4 })]
    [InlineData("   ", 5, new[] { 0, 1, 2, 3, 4 })]
    [InlineData("3", 5, new[] { 2 })]
    [InlineData("1-3", 5, new[] { 0, 1, 2 })]
    [InlineData("1-3, 5", 5, new[] { 0, 1, 2, 4 })]
    [InlineData("1-3, 5, 7-8", 8, new[] { 0, 1, 2, 4, 6, 7 })]
    [InlineData(" 2 - 3 ,5", 5, new[] { 1, 2, 4 })]
    [InlineData("2,2,1", 5, new[] { 1, 0 })]
    [InlineData("1-3,2-4", 5, new[] { 0, 1, 2, 3 })]
    [InlineData("3-3", 5, new[] { 2 })]
    public void TryParse_Valid(string text, int count, int[] expected)
    {
        Assert.True(PageRangeParser.TryParse(text, count, out var r));
        Assert.Equal(expected, r);
    }

    [Theory]
    [InlineData("0", 5)]
    [InlineData("6", 5)]
    [InlineData("5-3", 5)]
    [InlineData("a", 5)]
    [InlineData("1--2", 5)]
    [InlineData("1,", 5)]
    [InlineData(",1", 5)]
    [InlineData("1,,2", 5)]
    [InlineData("-3", 5)]
    [InlineData("1-", 5)]
    [InlineData("1-6", 5)]
    [InlineData("99999999999999", 5)]
    [InlineData("+1", 5)]
    public void TryParse_Invalid(string text, int count)
    {
        Assert.False(PageRangeParser.TryParse(text, count, out var r));
        Assert.Empty(r);
    }
}
