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
}
