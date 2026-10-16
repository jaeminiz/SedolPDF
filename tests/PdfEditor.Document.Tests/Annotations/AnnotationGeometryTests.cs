using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Document.Tests.Annotations;

public class AnnotationGeometryTests
{
    private static ImmutableArray<ImmutableArray<PdfPointF>> Stroke(params (float x, float y)[] pts)
        => [[.. pts.Select(p => new PdfPointF(p.x, p.y))]];

    private static Annotation Ink(float width = 2f, params (float x, float y)[] pts)
        => new() { Kind = AnnotKind.Ink, BorderWidth = width, InkStrokes = Stroke(pts) };

    [Fact]
    public void ComputeRect_Ink_IncludesHalfWidthPlusOne()
    {
        var a = Ink(2f, (10, 10), (30, 20));
        Assert.Equal(new PdfRectF(8, 8, 32, 22), AnnotationGeometry.ComputeRect(a));
    }

    [Fact]
    public void ComputeRect_Markup_UnionOfQuads()
    {
        var a = new Annotation
        {
            Kind = AnnotKind.Highlight,
            Quads = [PdfQuadF.FromRect(new PdfRectF(10, 10, 20, 20)), PdfQuadF.FromRect(new PdfRectF(15, 30, 50, 40))],
        };
        Assert.Equal(new PdfRectF(10, 10, 50, 40), AnnotationGeometry.ComputeRect(a));
    }

    [Fact]
    public void Translate_MovesRectQuadsAndInk()
    {
        var a = Ink(2f, (10, 10), (30, 20)) with
        {
            Rect = new PdfRectF(8, 8, 32, 22),
            Quads = [PdfQuadF.FromRect(new PdfRectF(0, 0, 5, 5))],
        };
        var m = AnnotationGeometry.Translate(a, 3, -2);
        Assert.Equal(new PdfRectF(11, 6, 35, 20), m.Rect);
        Assert.Equal(new PdfPointF(13, 8), m.InkStrokes[0][0]);
        Assert.Equal(new PdfPointF(33, 18), m.InkStrokes[0][1]);
        Assert.Equal(new PdfRectF(3, -2, 8, 3), m.Quads[0].Bounds);
    }

    [Fact]
    public void Resize_Ink_ScalesPointsIntoNewRect()
    {
        var a = Ink(2f, (10, 10), (30, 20));
        a = a with { Rect = AnnotationGeometry.ComputeRect(a) };
        // newRect minus margin (2*0.5+1 = 2) = (0,0)-(40,20)
        var r = AnnotationGeometry.Resize(a, new PdfRectF(-2, -2, 42, 22));
        Assert.Equal(new PdfPointF(0, 0), r.InkStrokes[0][0]);
        Assert.Equal(new PdfPointF(40, 20), r.InkStrokes[0][1]);
        Assert.Equal(new PdfRectF(-2, -2, 42, 22), r.Rect);
    }

    [Fact]
    public void Resize_Square_ClampsToMinSize()
    {
        var a = new Annotation { Kind = AnnotKind.Square, Rect = new PdfRectF(0, 0, 10, 10) };
        var r = AnnotationGeometry.Resize(a, new PdfRectF(0, 0, 0.5f, 10));
        Assert.Equal(AnnotationGeometry.MinShapeSizePt, r.Rect.Width);
        Assert.Equal(10f, r.Rect.Height);
    }

    [Fact]
    public void Resize_Highlight_NotSupported()
    {
        var a = new Annotation { Kind = AnnotKind.Highlight };
        Assert.False(AnnotationGeometry.CanResize(AnnotKind.Highlight));
        Assert.Throws<NotSupportedException>(() => AnnotationGeometry.Resize(a, new PdfRectF(0, 0, 10, 10)));
    }
}
