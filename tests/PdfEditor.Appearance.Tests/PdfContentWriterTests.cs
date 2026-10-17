using PdfEditor.Appearance.Fonts;
using System.Globalization;
using Pdf.Native;
using PdfEditor.Appearance;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance.Tests;

public class PdfContentWriterTests
{
    private static Annotation Sq(float opacity = 1f) => new()
    {
        Kind = AnnotKind.Square,
        Rect = new PdfRectF(0, 0, 100, 50),
        Color = PdfColor.FromHex("#FF0000"),
        BorderWidth = 2,
        Opacity = opacity,
    };

    [Fact]
    public void Square_ExactContentString()
    {
        var s = PdfContentWriter.Write(AnnotationPainter.Paint(Sq(), 0));
        Assert.Equal("q\n1 0 0 RG\n2 w\n0 J\n0 j\n1 1 m\n99 1 l\n99 49 l\n1 49 l\nh\nS\nQ\n", s);
    }

    [Fact]
    public void OpacityBelowOne_EmitsGsOnce()
    {
        var s = PdfContentWriter.Write(AnnotationPainter.Paint(Sq(0.5f), 0));
        Assert.Equal(1, s.Split("/GS gs").Length - 1);
        Assert.StartsWith("q\n/GS gs\n", s);
    }

    [Fact]
    public void InvariantCulture_UnderGermanCulture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var a = Sq() with { Rect = new PdfRectF(0.5f, 0.5f, 10.25f, 10.25f), BorderWidth = 0.5f, Color = PdfColor.FromHex("#808080") };
            var s = PdfContentWriter.Write(AnnotationPainter.Paint(a, 0));
            Assert.Contains("0.5 w", s);
            Assert.DoesNotContain(",", s);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    private static AppearanceDrawing Empty() => new([], 1f, new PdfRectF(0, 0, 100, 50));

    [Fact]
    public void Writer_TextRun_ExactString()
    {
        var d = Empty() with
        {
            Texts =
            [
                new TextRunOp("F1", 12f, PdfColor.FromHex("#FF0000"),
                [
                    new PositionedGlyph(5, 0x1234, new PdfPointF(10f, 20.5f), 6f, "A"),
                    new PositionedGlyph(6, 0x00AB, new PdfPointF(16.25f, 20.5f), 6f, "B"),
                ]),
            ],
        };
        Assert.Equal(
            "q\nBT\n/F1 12 Tf\n1 0 0 rg\n1 0 0 1 10 20.5 Tm\n<1234> Tj\n1 0 0 1 16.25 20.5 Tm\n<00AB> Tj\nET\nQ\n",
            PdfContentWriter.Write(d));
    }

    [Fact]
    public void Writer_Image_ExactString()
    {
        var d = Empty() with { Images = [new ImageOp("Im1", "img-1", new PdfRectF(10, 20, 110, 70))] };
        Assert.Equal("q\nq\n100 0 0 50 10 20 cm\n/Im1 Do\nQ\nQ\n", PdfContentWriter.Write(d));
    }

    [Fact]
    public void Writer_Order_PathsThenImagesThenText()
    {
        var sq = AnnotationPainter.Paint(Sq(), 0);
        var d = sq with
        {
            Images = [new ImageOp("Im1", "i", new PdfRectF(0, 0, 10, 10))],
            Texts = [new TextRunOp("F1", 10f, PdfColor.FromHex("#000000"), [new PositionedGlyph(1, 1, new PdfPointF(0, 0), 5f, "A")])],
        };
        var s = PdfContentWriter.Write(d);
        int iPath = s.IndexOf("\nS\n", StringComparison.Ordinal), iImg = s.IndexOf("/Im1 Do", StringComparison.Ordinal), iTxt = s.IndexOf("BT\n", StringComparison.Ordinal);
        Assert.True(iPath >= 0 && iPath < iImg && iImg < iTxt);
    }

    [Fact]
    public void CollectFontUse_GathersCodepointsCidsAndWidths()
    {
        var d = Empty() with
        {
            Texts =
            [
                new TextRunOp("F1", 10f, PdfColor.FromHex("#000000"),
                [
                    new PositionedGlyph(5, 77, new PdfPointF(0, 0), 5f, "A"),
                    new PositionedGlyph(9, 88, new PdfPointF(5, 0), 10f, "¬"),
                    new PositionedGlyph(5, 77, new PdfPointF(15, 0), 5f, "A"),
                    new PositionedGlyph(10, 99, new PdfPointF(20, 0), 3f, ""),
                ]),
            ],
        };
        var use = d.CollectFontUse();
        Assert.Equal([0x41, 0xAC], use.Codepoints.ToArray());
        Assert.Equal([(77, 500, "A"), (88, 1000, "¬"), (99, 300, "")],
            use.Glyphs.Select(g => (g.Cid, g.Advance1000, g.Text)).ToArray());
        Assert.Empty(Empty().CollectFontUse().Glyphs);
    }
}
