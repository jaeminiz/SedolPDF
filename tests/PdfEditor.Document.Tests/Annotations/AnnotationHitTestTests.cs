using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Document.Tests.Annotations;

public class AnnotationHitTestTests
{
    [Fact]
    public void Ink_HitWithinHalfWidthPlusTolerance()
    {
        var a = new Annotation
        {
            Kind = AnnotKind.Ink,
            BorderWidth = 4f,
            InkStrokes = [[new PdfPointF(0, 0), new PdfPointF(100, 0)]],
        };
        // threshold = 2 + 3 = 5
        Assert.True(AnnotationHitTest.Hit(a, new PdfPointF(50, 4.9f), 3f));
        Assert.False(AnnotationHitTest.Hit(a, new PdfPointF(50, 5.5f), 3f));
        Assert.False(AnnotationHitTest.Hit(a, new PdfPointF(110, 0), 3f));
    }

    [Fact]
    public void Square_NoFill_InteriorNotHit_BorderHit()
    {
        var a = new Annotation { Kind = AnnotKind.Square, Rect = new PdfRectF(0, 0, 100, 100), BorderWidth = 2f };
        Assert.False(AnnotationHitTest.Hit(a, new PdfPointF(50, 50), 2f));
        Assert.True(AnnotationHitTest.Hit(a, new PdfPointF(50, 1), 2f));
        Assert.True(AnnotationHitTest.Hit(a, new PdfPointF(99, 50), 2f));
    }

    [Fact]
    public void Square_Filled_InteriorHit()
    {
        var a = new Annotation
        {
            Kind = AnnotKind.Square,
            Rect = new PdfRectF(0, 0, 100, 100),
            FillColor = new PdfColor(255, 0, 0),
        };
        Assert.True(AnnotationHitTest.Hit(a, new PdfPointF(50, 50), 2f));
        Assert.False(AnnotationHitTest.Hit(a, new PdfPointF(150, 50), 2f));
    }

    [Fact]
    public void Circle_BorderHit_CornerOfRectNotHit()
    {
        var a = new Annotation { Kind = AnnotKind.Circle, Rect = new PdfRectF(0, 0, 100, 100), BorderWidth = 2f };
        Assert.True(AnnotationHitTest.Hit(a, new PdfPointF(50, 1), 2f));
        Assert.True(AnnotationHitTest.Hit(a, new PdfPointF(99, 50), 2f));
        Assert.False(AnnotationHitTest.Hit(a, new PdfPointF(2, 2), 2f));
        Assert.False(AnnotationHitTest.Hit(a, new PdfPointF(50, 50), 2f));
    }

    [Fact]
    public void TopmostAt_ReturnsLastDrawn()
    {
        var a1 = new Annotation { Id = new AnnotId(1), Kind = AnnotKind.Text, Rect = new PdfRectF(0, 0, 20, 20) };
        var a2 = a1 with { Id = new AnnotId(2) };
        Assert.Equal(new AnnotId(2), AnnotationHitTest.TopmostAt([a1, a2], new PdfPointF(10, 10), 1f));
        Assert.Null(AnnotationHitTest.TopmostAt([a1, a2], new PdfPointF(100, 100), 1f));
    }
}
