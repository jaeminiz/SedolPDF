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

    [Fact]
    public void HitTest_PolyLineSegment()
    {
        var a = new Annotation
        {
            Kind = AnnotKind.PolyLine, BorderWidth = 2f,
            Vertices = [new PdfPointF(0, 0), new PdfPointF(100, 0), new PdfPointF(100, 100)],
        };
        Assert.True(AnnotationHitTest.Hit(a, new PdfPointF(50, 3.9f), 3f));
        Assert.True(AnnotationHitTest.Hit(a, new PdfPointF(103.9f, 50), 3f));
        Assert.False(AnnotationHitTest.Hit(a, new PdfPointF(50, 5f), 3f));
        Assert.False(AnnotationHitTest.Hit(a, new PdfPointF(50, 50), 3f));
    }

    [Fact]
    public void HitTest_Line_UsesSegmentDistance()
    {
        var a = new Annotation { Kind = AnnotKind.Line, BorderWidth = 2f, Vertices = [new PdfPointF(0, 0), new PdfPointF(100, 100)] };
        Assert.True(AnnotationHitTest.Hit(a, new PdfPointF(50, 52), 3f));
        Assert.False(AnnotationHitTest.Hit(a, new PdfPointF(50, 60), 3f));
    }

    [Fact]
    public void HitTest_FreeTextBoxOrLeader()
    {
        var a = new Annotation
        {
            Kind = AnnotKind.FreeText, BorderWidth = 1f, Intent = FreeTextIntent.Callout,
            TextBox = new PdfRectF(100, 100, 200, 150),
            Vertices = [new PdfPointF(20, 20), new PdfPointF(100, 100)],
        };
        Assert.True(AnnotationHitTest.Hit(a, new PdfPointF(150, 125), 2f));
        Assert.True(AnnotationHitTest.Hit(a, new PdfPointF(60, 60), 2f));
        Assert.False(AnnotationHitTest.Hit(a, new PdfPointF(60, 90), 2f));
        Assert.False(AnnotationHitTest.Hit(a, new PdfPointF(250, 125), 2f));
    }

    [Fact]
    public void HitTest_Polygon_FillInsideVsEdgeOnly()
    {
        var verts = ImmutableArray.Create(new PdfPointF(0, 0), new PdfPointF(100, 0), new PdfPointF(100, 100), new PdfPointF(0, 100));
        var open = new Annotation { Kind = AnnotKind.Polygon, BorderWidth = 2f, Vertices = verts };
        var filled = open with { FillColor = new PdfColor(255, 0, 0) };
        Assert.False(AnnotationHitTest.Hit(open, new PdfPointF(50, 50), 2f));
        Assert.True(AnnotationHitTest.Hit(open, new PdfPointF(0, 50), 2f));
        Assert.True(AnnotationHitTest.Hit(filled, new PdfPointF(50, 50), 2f));
        Assert.False(AnnotationHitTest.Hit(filled, new PdfPointF(150, 50), 2f));
    }

    [Theory]
    [InlineData(AnnotKind.Caret)]
    [InlineData(AnnotKind.Stamp)]
    [InlineData(AnnotKind.FileAttachment)]
    public void HitTest_RectKinds(AnnotKind k)
    {
        var a = new Annotation { Kind = k, Rect = new PdfRectF(0, 0, 20, 20) };
        Assert.True(AnnotationHitTest.Hit(a, new PdfPointF(10, 10), 2f));
        Assert.False(AnnotationHitTest.Hit(a, new PdfPointF(30, 10), 2f));
    }
}
