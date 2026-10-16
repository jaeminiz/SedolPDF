using PdfEditor.Document.Annotations;

namespace PdfEditor.Document.Tests.Annotations;

public class PdfDateTests
{
    [Theory]
    [InlineData("D:20261016143005+09'00'", 2026, 10, 16, 14, 30, 5, 9)]
    [InlineData("D:20261016143005Z", 2026, 10, 16, 14, 30, 5, 0)]
    [InlineData("D:20261016143005+0900", 2026, 10, 16, 14, 30, 5, 9)]
    [InlineData("D:20261016143005+09'00", 2026, 10, 16, 14, 30, 5, 9)]
    [InlineData("D:20261016143005-05'00'", 2026, 10, 16, 14, 30, 5, -5)]
    public void TryParse_WithZone(string s, int y, int mo, int d, int h, int mi, int sec, int offHours)
    {
        Assert.True(PdfDate.TryParse(s, out var t));
        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, sec, TimeSpan.FromHours(offHours)), t);
    }

    [Fact]
    public void TryParse_DateOnly_NoPrefix()
    {
        Assert.True(PdfDate.TryParse("20261016", out var t));
        Assert.Equal(new DateTime(2026, 10, 16), t.DateTime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(null)]
    [InlineData("D:20261316")]
    public void TryParse_Invalid(string? s) => Assert.False(PdfDate.TryParse(s, out _));

    [Fact]
    public void Format_Matches()
    {
        var t = new DateTimeOffset(2026, 10, 16, 14, 30, 5, TimeSpan.FromHours(9));
        Assert.Equal("D:20261016143005+09'00'", PdfDate.Format(t));
    }

    [Fact]
    public void Format_RoundTripsThroughTryParse()
    {
        var t = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromMinutes(-210));
        Assert.True(PdfDate.TryParse(PdfDate.Format(t), out var back));
        Assert.Equal(t, back);
        Assert.Equal(t.Offset, back.Offset);
    }
}
