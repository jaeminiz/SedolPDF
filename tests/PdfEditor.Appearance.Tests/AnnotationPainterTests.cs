using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Appearance;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance.Tests;

public class AnnotationPainterTests
{
    private static readonly PdfColor Red = PdfColor.FromHex("#FF0000");
    private const float Eps = 1e-3f;

    private static ImmutableArray<ImmutableArray<PdfPointF>> Strokes(params PdfPointF[][] s)
        => [.. s.Select(x => ImmutableArray.CreateRange(x))];

    private static PdfPointF P(float x, float y) => new(x, y);

    private static void AssertPt(PdfPointF expected, PdfPointF actual)
    {
        Assert.InRange(actual.X, expected.X - Eps, expected.X + Eps);
        Assert.InRange(actual.Y, expected.Y - Eps, expected.Y + Eps);
    }

    private static Annotation Ink(params PdfPointF[][] s) => new()
    {
        Kind = AnnotKind.Ink,
        Color = Red,
        BorderWidth = 2,
        InkStrokes = Strokes(s),
        Rect = new PdfRectF(0, 0, 1, 1),
    };

    [Fact]
    public void Ink_ThreePoints_CatmullRomControlPointsExact()
    {
        var d = AnnotationPainter.Paint(Ink([P(0, 0), P(10, 10), P(20, 0)]), 0);
        var op = Assert.Single(d.Ops);
        Assert.Equal(LineCap.Round, op.Cap);
        Assert.Equal(LineJoin.Round, op.Join);
        Assert.Null(op.Fill);
        Assert.Equal(Red, op.Stroke);
        Assert.Equal(2f, op.StrokeWidth);
        var cubics = op.Path.OfType<CubicTo>().ToArray();
        Assert.Equal(2, cubics.Length);
        AssertPt(P(10f / 6, 10f / 6), cubics[0].C1);
        // C2 = P1 - (P2 - P0)/6
        AssertPt(P(10 - 20f / 6, 10), cubics[0].C2);
        AssertPt(P(10, 10), cubics[0].P);
        AssertPt(P(20, 0), cubics[1].P);
    }

    [Fact]
    public void Ink_SinglePoint_IsDotWithRoundCap()
    {
        var d = AnnotationPainter.Paint(Ink([P(5, 5)]), 0);
        var op = Assert.Single(d.Ops);
        Assert.Equal(LineCap.Round, op.Cap);
        Assert.Equal(new PathSeg[] { new MoveTo(P(5, 5)), new LineTo(P(5, 5)) }, op.Path.ToArray());
    }

    [Fact]
    public void Square_InsetByHalfWidth_FillAndStroke()
    {
        var a = new Annotation
        {
            Kind = AnnotKind.Square,
            Rect = new PdfRectF(0, 0, 100, 50),
            Color = Red,
            FillColor = PdfColor.FromHex("#00FF00"),
            BorderWidth = 2,
        };
        var op = Assert.Single(AnnotationPainter.Paint(a, 0).Ops);
        Assert.Equal(PdfColor.FromHex("#00FF00"), op.Fill);
        Assert.Equal(Red, op.Stroke);
        Assert.Equal(LineJoin.Miter, op.Join);
        Assert.Equal(
            new PathSeg[] { new MoveTo(P(1, 1)), new LineTo(P(99, 1)), new LineTo(P(99, 49)), new LineTo(P(1, 49)), new ClosePath() },
            op.Path.ToArray());
    }

    [Fact]
    public void Square_NoColorNoFill_NoOps()
    {
        var a = new Annotation { Kind = AnnotKind.Square, Rect = new PdfRectF(0, 0, 100, 50), BorderWidth = 2 };
        Assert.Empty(AnnotationPainter.Paint(a, 0).Ops);
    }

    [Fact]
    public void Circle_FourCubics_KappaExact()
    {
        const float k = 0.5522847498f;
        var a = new Annotation { Kind = AnnotKind.Circle, Rect = new PdfRectF(0, 0, 100, 50), Color = Red, BorderWidth = 2 };
        var op = Assert.Single(AnnotationPainter.Paint(a, 0).Ops);
        Assert.Equal(new MoveTo(P(99, 25)), op.Path[0]);
        var c = op.Path.OfType<CubicTo>().ToArray();
        Assert.Equal(4, c.Length);
        float cx = 50, cy = 25, rx = 49, ry = 24;
        AssertPt(P(cx + rx, cy + k * ry), c[0].C1);
        AssertPt(P(cx + k * rx, cy + ry), c[0].C2);
        AssertPt(P(cx, cy + ry), c[0].P);
        AssertPt(P(cx - rx, cy), c[1].P);
        AssertPt(P(cx, cy - ry), c[2].P);
        AssertPt(P(cx + rx, cy), c[3].P);
    }

    [Fact]
    public void Highlight_OneFilledPolygonPerQuad_P1P2P4P3()
    {
        var q1 = PdfQuadF.FromRect(new PdfRectF(0, 0, 10, 5));
        var q2 = PdfQuadF.FromRect(new PdfRectF(0, 10, 10, 15));
        var a = new Annotation { Kind = AnnotKind.Highlight, Color = Red, Quads = [q1, q2], Rect = new PdfRectF(0, 0, 10, 15) };
        var ops = AnnotationPainter.Paint(a, 0).Ops;
        Assert.Equal(2, ops.Length);
        Assert.All(ops, o => { Assert.Equal(Red, o.Fill); Assert.Null(o.Stroke); });
        Assert.Equal(
            new PathSeg[] { new MoveTo(q1.P1), new LineTo(q1.P2), new LineTo(q1.P4), new LineTo(q1.P3), new ClosePath() },
            ops[0].Path.ToArray());
    }

    [Fact]
    public void Underline_LineNearBottom_WidthFromHeight()
    {
        var q = PdfQuadF.FromRect(new PdfRectF(0, 100, 50, 120));
        var a = new Annotation { Kind = AnnotKind.Underline, Color = Red, Quads = [q], Rect = new PdfRectF(0, 100, 50, 120) };
        var op = Assert.Single(AnnotationPainter.Paint(a, 0).Ops);
        Assert.Equal(1.4f, op.StrokeWidth, 1e-4f);
        Assert.Equal(LineCap.Butt, op.Cap);
        Assert.Equal(Red, op.Stroke);
        AssertPt(P(0, 100 + 0.7f + 0.8f), ((MoveTo)op.Path[0]).P);
        AssertPt(P(50, 100 + 0.7f + 0.8f), ((LineTo)op.Path[1]).P);
    }

    [Fact]
    public void StrikeOut_LineAt45Percent()
    {
        var q = PdfQuadF.FromRect(new PdfRectF(0, 100, 50, 120));
        var a = new Annotation { Kind = AnnotKind.StrikeOut, Color = Red, Quads = [q], Rect = new PdfRectF(0, 100, 50, 120) };
        var op = Assert.Single(AnnotationPainter.Paint(a, 0).Ops);
        AssertPt(P(0, 109), ((MoveTo)op.Path[0]).P);
        AssertPt(P(50, 109), ((LineTo)op.Path[1]).P);
        Assert.Equal(1.4f, op.StrokeWidth, 1e-4f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TextIcon_UprightForAllRotations(int rotation)
    {
        var rect = new PdfRectF(100, 200, 120, 220);
        var a = new Annotation { Kind = AnnotKind.Text, Rect = rect, Color = PdfColor.FromHex("#FFD400") };
        var d = AnnotationPainter.Paint(a, rotation);
        // Ops[0] = body, Ops[1] = first line
        var bc = PdfRectF.FromPoints(d.Ops[0].Path.SelectMany(PointsOf));
        var line = d.Ops[1];
        var l0 = ((MoveTo)line.Path[0]).P;
        var l1 = ((LineTo)line.Path[1]).P;

        // PageTransform follows the RectangleF.FromLTRB(l, b, r, t) convention.
        var bounds = System.Drawing.RectangleF.FromLTRB(0, 0, 612, 792);
        var tf = new PageTransform(bounds, rotation, 1);
        var v0 = tf.ToView(l0.X, l0.Y);
        var v1 = tf.ToView(l1.X, l1.Y);
        var vc = tf.ToView((bc.Left + bc.Right) / 2, (bc.Bottom + bc.Top) / 2);

        Assert.True(v0.Y < vc.Y, $"first line y {v0.Y} should be above center y {vc.Y}");
        Assert.True(Math.Abs(v0.Y - v1.Y) < 0.01, "first line must be horizontal on screen");
        Assert.True(Math.Abs(v0.X - v1.X) > 5);
    }

    private static IEnumerable<PdfPointF> PointsOf(PathSeg s) => s switch
    {
        MoveTo m => [m.P],
        LineTo l => [l.P],
        CubicTo c => [c.C1, c.C2, c.P],
        _ => [],
    };

    [Fact]
    public void Bounds_WithinComputeRect_ForAllKinds()
    {
        var quad = PdfQuadF.FromRect(new PdfRectF(10, 100, 60, 120));
        var list = new List<Annotation>
        {
            Ink([P(0, 0), P(10, 10), P(20, 0), P(30, 15)]),
            new() { Kind = AnnotKind.Square, Rect = new PdfRectF(0, 0, 100, 50), Color = Red, FillColor = Red, BorderWidth = 4 },
            new() { Kind = AnnotKind.Circle, Rect = new PdfRectF(0, 0, 100, 50), Color = Red, BorderWidth = 4 },
            new() { Kind = AnnotKind.Highlight, Color = Red, Quads = [quad], Rect = quad.Bounds },
            new() { Kind = AnnotKind.Underline, Color = Red, Quads = [quad], Rect = quad.Bounds },
            new() { Kind = AnnotKind.StrikeOut, Color = Red, Quads = [quad], Rect = quad.Bounds },
            new() { Kind = AnnotKind.Text, Color = PdfColor.FromHex("#FFD400"), Rect = new PdfRectF(0, 0, 20, 20) },
        };
        foreach (var a in list)
        {
            var b = AnnotationPainter.Paint(a, 0).Bounds;
            var r = AnnotationGeometry.ComputeRect(a).Inflate(0.01f);
            Assert.True(b.Left >= r.Left && b.Bottom >= r.Bottom && b.Right <= r.Right && b.Top <= r.Top,
                $"{a.Kind}: {b} not within {r}");
        }
    }

    [Fact]
    public void Opacity_CarriedToDrawing()
    {
        var a = new Annotation { Kind = AnnotKind.Square, Rect = new PdfRectF(0, 0, 10, 10), Color = Red, Opacity = 0.4f };
        Assert.Equal(0.4f, AnnotationPainter.Paint(a, 0).Opacity);
    }
}
