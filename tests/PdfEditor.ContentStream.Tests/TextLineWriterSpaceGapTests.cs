using System.Text;
using PdfEditor.ContentStream;
using PdfEditor.ContentStream.Fonts;

namespace PdfEditor.ContentStream.Tests;

/// <summary>0.13.0: 공백 코드가 없는 글꼴(공백을 간격으로 그린 도면) — 공백은 글 코드 없이 TJ 간격으로 쓴다.</summary>
public sealed class TextLineWriterSpaceGapTests
{
    [Fact]
    public void SpacesBecomeTjGaps_AndBytesStayAligned()
    {
        var enc = new EncodedText("AB C", [0x41, 0x42, 0x43], default);
        string s = Encoding.Latin1.GetString(TextLineWriter.Write(false, "F1", enc, new TextLineStyle(10), Mat.Identity, null, 250));
        Assert.Contains("[<4142> -250 <43>] TJ", s);
    }

    [Fact]
    public void NoGap_WritesPlainTj()
    {
        var enc = new EncodedText("AB", [0x41, 0x42], default);
        string s = Encoding.Latin1.GetString(TextLineWriter.Write(false, "F1", enc, new TextLineStyle(10), Mat.Identity));
        Assert.Contains("<4142> Tj", s);
    }
}
