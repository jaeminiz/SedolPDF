using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;

namespace PdfEditor.Document.Tests.Annotations;

public class DefaultStyleTests
{
    [Fact]
    public void Parse_AcrobatShorthand()
    {
        var (size, color, family) = DefaultStyle.Parse("font: 500 'Adobe Myungjo Std',serif 12.0pt; text-align:left; color:#FF0000 ");
        Assert.Equal(12f, size);
        Assert.Equal(new PdfColor(255, 0, 0), color);
        Assert.Contains("Myungjo", family);
    }

    [Fact]
    public void Parse_Longhand()
    {
        var (size, color, family) = DefaultStyle.Parse("line-height:18pt;font-family:맑은 고딕;font-size:12pt;color:#FF0000;");
        Assert.Equal(12f, size);
        Assert.Equal(new PdfColor(255, 0, 0), color);
        Assert.Equal("맑은 고딕", family);
    }

    [Fact]
    public void Parse_Garbage_ReturnsNulls()
    {
        Assert.Equal((null, null, null), DefaultStyle.Parse("color:#GG"));
        Assert.Equal((null, null, null), DefaultStyle.Parse(null));
        Assert.Equal((null, null, null), DefaultStyle.Parse("nonsense"));
    }

    [Theory]
    [InlineData("sans-serif", TextFontFamily.Sans)]
    [InlineData("'Malgun Gothic',sans-serif", TextFontFamily.Sans)]
    [InlineData("AdobeMyungjoStd-Medium", TextFontFamily.Serif)]
    [InlineData("'Adobe Myungjo Std M'", TextFontFamily.Serif)]
    [InlineData("'Times New Roman',serif", TextFontFamily.Serif)]
    [InlineData("바탕", TextFontFamily.Serif)]
    [InlineData("Helv", TextFontFamily.Sans)]
    public void FamilyOf(string text, TextFontFamily expected) => Assert.Equal(expected, DefaultStyle.FamilyOf(text));

    [Fact]
    public void FamilyOf_AnyTextCounts()
        => Assert.Equal(TextFontFamily.Serif, DefaultStyle.FamilyOf(null, "sans-serif", "Batang"));
}
