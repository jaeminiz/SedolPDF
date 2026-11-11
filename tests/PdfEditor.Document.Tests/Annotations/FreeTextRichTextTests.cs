using System.Collections.Immutable;
using System.Xml.Linq;
using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;

namespace PdfEditor.Document.Tests.Annotations;

/// <summary>0.10.0 Task 4: FreeText의 /DA·/DS·/RC를 Acrobat 관례로 쓴다(spec 4.2).</summary>
public class FreeTextRichTextTests
{
    private static Annotation Box(Action<Annotation>? _ = null) => new()
    {
        Kind = AnnotKind.FreeText, Rect = new PdfRectF(10, 10, 200, 60), Contents = "Hello",
        Color = new PdfColor(255, 0, 0), BorderWidth = 1f, TextColor = new PdfColor(0, 0, 0), FontSizePt = 20f,
    };

    [Fact]
    public void Da_RedBorderBlackText()
        => Assert.Equal("1 0 0 rg 1 0 0 RG /Helv 20 Tf", FreeTextRichText.Da(Box()));

    [Fact]
    public void NoBorderWidth_DaRgIsTextColour()
    {
        var a = Box() with { BorderWidth = 0f, TextColor = new PdfColor(0, 0, 255), FontSizePt = 12f };
        Assert.Equal("0 0 1 rg /Helv 12 Tf", FreeTextRichText.Da(a));
    }

    [Fact]
    public void NoBorderColor_DaRgIsTextColour()
    {
        var a = Box() with { Color = null, TextColor = new PdfColor(0, 0, 255), FontSizePt = 12f };
        Assert.Equal("0 0 1 rg /Helv 12 Tf", FreeTextRichText.Da(a));
    }

    [Fact]
    public void Ds_SerifBoldUnderlineCentre()
    {
        var a = Box() with
        {
            TextFamily = TextFontFamily.Serif, FontSizePt = 10.5f, TextColor = new PdfColor(255, 0, 0),
            Align = TextAlign.Center, Bold = true, Underline = true,
        };
        Assert.Equal("font-family:'Adobe Myungjo Std','Batang',serif;font-size:10.5pt;color:#FF0000;text-align:center;font-weight:bold;text-decoration:underline",
            FreeTextRichText.Ds(a));
    }

    [Fact]
    public void Ds_Sans12_NoFormatting_AndBothDecorations()
    {
        Assert.Equal("font-family:'Malgun Gothic',sans-serif;font-size:12pt;color:#000000;text-align:left", FreeTextRichText.Ds(Box() with { FontSizePt = 12f }));
        Assert.EndsWith(";font-style:italic;text-decoration:underline line-through",
            FreeTextRichText.Ds(Box() with { Italic = true, Underline = true, StrikeThrough = true }));
    }

    [Fact]
    public void Rc_TwoLines_HasStyleAndParagraphs()
    {
        var a = Box() with { Contents = "one\r\ntwo" };
        var doc = XDocument.Parse(FreeTextRichText.Rc(a, "0.10.0"));
        XNamespace x = "http://www.w3.org/1999/xhtml", xfa = "http://www.xfa.org/schema/xfa-data/1.0/";
        var body = doc.Root!;
        Assert.Equal(x + "body", body.Name);
        Assert.Equal(FreeTextRichText.Ds(a), (string)body.Attribute("style")!);
        Assert.Equal("SedolPDF:0.10.0", (string)body.Attribute(xfa + "APIVersion")!);
        var ps = body.Elements(x + "p").ToList();
        Assert.Equal(["one", "two"], ps.Select(p => p.Value));
        Assert.All(ps, p => Assert.Equal("ltr", (string)p.Attribute("dir")!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("a&b<c>\"d")]
    public void RichText_EmptyAndEscapes_AreWellFormedXml(string contents)
    {
        var a = Box() with { Contents = contents };
        var doc = XDocument.Parse(FreeTextRichText.Rc(a, "0.10.0"));
        XNamespace x = "http://www.w3.org/1999/xhtml";
        var ps = doc.Root!.Elements(x + "p").ToList();
        Assert.Equal(contents.Replace("\r\n", "\n").Split('\n').Length, ps.Count);
        Assert.Equal(contents.Replace("\r\n", "\n"), string.Join("\n", ps.Select(p => p.Value)));
        foreach (var (p, line) in ps.Zip(contents.Split('\n'))) if (line.Length == 0) Assert.NotNull(p.Element(x + "br"));
    }

    [Fact]
    public void FreeTextOps_WritesDaDsRc()
    {
        var a = Box();
        var ap = new PatchAppearance("", new PdfRectF(0, 0, 190, 50), new FontUse([], []));
        var ops = PatchPlanBuilder.FreeTextOps(a, ap, isNew: true);
        string Get(string key) => ops.OfType<SetString>().Single(o => o.Key == key).Value;
        Assert.Equal(FreeTextRichText.Da(a), Get("/DA"));
        Assert.Equal(FreeTextRichText.Ds(a), Get("/DS"));
        Assert.Equal(FreeTextRichText.Rc(a, FreeTextRichText.AppVersion), Get("/RC"));
    }
}
