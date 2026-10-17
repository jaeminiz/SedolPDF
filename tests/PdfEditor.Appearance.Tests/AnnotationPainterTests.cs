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

    // ---- M3a Task 7: line endings, polylines, polygons, clouds, caret, attachment icon ----

    private static Annotation LineAnn(PdfPointF a, PdfPointF b, LineEnding start = LineEnding.None, LineEnding end = LineEnding.None, float w = 2)
        => new()
        {
            Kind = AnnotKind.Line, Color = Red, BorderWidth = w, Vertices = [a, b], StartEnding = start, EndEnding = end,
            Rect = new PdfRectF(0, 0, 1, 1),
        };

    private static float Deg(float rad) => rad * 180f / MathF.PI;

    private static void AssertWithin(PdfRectF b, PdfRectF r, string what)
    {
        r = r.Inflate(0.01f);
        Assert.True(b.Left >= r.Left && b.Bottom >= r.Bottom && b.Right <= r.Right && b.Top <= r.Top, $"{what}: {b} not within {r}");
    }

    [Fact]
    public void Line_OpenArrow_WingsAt30Degrees()
    {
        var a = LineAnn(P(0, 0), P(100, 0), end: LineEnding.OpenArrow);
        var ops = AnnotationPainter.Paint(a, 0).Ops;
        Assert.Equal(2, ops.Length);
        Assert.Equal(2, ops[0].Path.Length);                       // shaft is not shortened
        AssertPt(P(100, 0), ((LineTo)ops[0].Path[1]).P);
        float len = AnnotationGeometry.EndingSize(a);               // 9
        var wing = ops[1].Path;
        var w1 = ((MoveTo)wing[0]).P; var tip = ((LineTo)wing[1]).P; var w2 = ((LineTo)wing[2]).P;
        AssertPt(P(100, 0), tip);
        foreach (var w in new[] { w1, w2 })
        {
            float dx = w.X - tip.X, dy = w.Y - tip.Y;
            Assert.Equal(len, MathF.Sqrt(dx * dx + dy * dy), 1e-3f);
            Assert.Equal(30f, Deg(MathF.Atan2(MathF.Abs(dy), -dx)), 1e-2f);   // angle against the backwards shaft direction
        }
        Assert.True(w1.Y * w2.Y < 0, "wings are on opposite sides");
    }

    [Fact]
    public void Line_ClosedArrow_ShaftStopsAtBase()
    {
        var a = LineAnn(P(0, 0), P(100, 0), end: LineEnding.ClosedArrow);
        var ops = AnnotationPainter.Paint(a, 0).Ops;
        float len = AnnotationGeometry.EndingSize(a);
        float baseX = 100 - len * MathF.Cos(MathF.PI / 6);
        Assert.Equal(baseX, ((LineTo)ops[0].Path[1]).P.X, 1e-3f);
        var tri = ops[1];
        Assert.Equal(Red, tri.Fill);                                // FillColor null -> Color
        Assert.Equal(Red, tri.Stroke);
        Assert.IsType<ClosePath>(tri.Path[^1]);
        Assert.Equal(baseX, ((LineTo)tri.Path[1]).P.X, 1e-3f);
        AssertPt(P(100, 0), ((MoveTo)tri.Path[0]).P);
        var green = PdfColor.FromHex("#00FF00");
        var ops2 = AnnotationPainter.Paint(a with { FillColor = green }, 0).Ops;
        Assert.Equal(green, ops2[1].Fill);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void AllEightEndings_BoundsWithinComputeRect(int shape)
    {
        foreach (var e in Enum.GetValues<LineEnding>())
            foreach (float w in new[] { 1f, 2f, 6f })
            {
                PdfPointF[] v = shape switch
                {
                    0 => [P(10, 10), P(90, 10)],
                    1 => [P(10, 10), P(90, 65)],
                    _ => [P(90, 70), P(15, 12)],
                };
                var line = LineAnn(v[0], v[1], e, e, w) with { FillColor = PdfColor.FromHex("#00FF00") };
                var poly = line with { Kind = AnnotKind.PolyLine, Vertices = [v[0], P(60, 40), P(110, 5), v[1]] };
                foreach (var a in new[] { line, poly })
                    AssertWithin(AnnotationPainter.Paint(a, 0).Bounds, AnnotationGeometry.ComputeRect(a), $"{a.Kind}/{e}/w{w}");
            }
    }

    [Fact]
    public void Polygon_ClosedFillAndStroke()
    {
        var green = PdfColor.FromHex("#00FF00");
        var a = new Annotation
        {
            Kind = AnnotKind.Polygon, Color = Red, FillColor = green, BorderWidth = 3,
            Vertices = [P(0, 0), P(50, 0), P(25, 40)], Rect = new PdfRectF(0, 0, 1, 1),
        };
        var d = AnnotationPainter.Paint(a, 0);
        var op = Assert.Single(d.Ops);
        Assert.Equal(green, op.Fill);
        Assert.Equal(Red, op.Stroke);
        Assert.Equal(3f, op.StrokeWidth);
        Assert.IsType<ClosePath>(op.Path[^1]);
        Assert.Equal(2, op.Path.OfType<LineTo>().Count());
        AssertWithin(d.Bounds, AnnotationGeometry.ComputeRect(a), "polygon");
    }

    [Fact]
    public void Cloud_Square_ArcCountPerEdge()
    {
        var a = new Annotation
        {
            Kind = AnnotKind.Square, Rect = new PdfRectF(0, 0, 100, 60), Color = Red, BorderWidth = 2, CloudIntensity = 1,
        };
        var d = AnnotationPainter.Paint(a, 0);
        var op = Assert.Single(d.Ops);
        // d = 10, RD = 5 + 1 = 6 -> inner rectangle 88 x 48 -> ceil(8.8) = 9 and ceil(4.8) = 5 arcs per edge, 2 Beziers each
        Assert.Equal((9 + 5) * 2 * 2, op.Path.OfType<CubicTo>().Count());
        Assert.Single(op.Path.OfType<MoveTo>());
        Assert.IsType<ClosePath>(op.Path[^1]);
        AssertWithin(d.Bounds, AnnotationGeometry.ComputeRect(a), "cloud square");
        var op2 = Assert.Single(AnnotationPainter.Paint(a with { CloudIntensity = 2 }, 0).Ops);
        Assert.True(op2.Path.OfType<CubicTo>().Count() < op.Path.OfType<CubicTo>().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cloud_BulgesOutward_ForBothWindings(bool clockwise)
    {
        PdfPointF[] sq = [P(0, 0), P(100, 0), P(100, 100), P(0, 100)];
        if (clockwise) Array.Reverse(sq);
        var a = new Annotation
        {
            Kind = AnnotKind.Polygon, Color = Red, BorderWidth = 1, CloudIntensity = 1, Vertices = [.. sq],
            Rect = new PdfRectF(0, 0, 1, 1),
        };
        var d = AnnotationPainter.Paint(a, 0);
        var cubics = Assert.Single(d.Ops).Path.OfType<CubicTo>().ToArray();
        static bool Inside(PdfPointF p) => p.X > 0.01f && p.X < 99.99f && p.Y > 0.01f && p.Y < 99.99f;
        int apexes = 0;
        for (int i = 0; i < cubics.Length; i += 2)
        {
            Assert.False(Inside(cubics[i].P), $"apex {cubics[i].P} lies inside the polygon");   // first Bezier ends at the apex
            apexes++;
        }
        Assert.Equal(40, apexes);                                    // 10 semicircles per 100 pt edge
        foreach (var c in cubics) Assert.False(Inside(c.C1) || Inside(c.C2));
        AssertWithin(d.Bounds, AnnotationGeometry.ComputeRect(a), "cloud polygon");
    }

    [Fact]
    public void Caret_Triangle()
    {
        var a = new Annotation { Kind = AnnotKind.Caret, Color = Red, Rect = new PdfRectF(10, 20, 30, 40) };
        var d = AnnotationPainter.Paint(a, 0);
        var op = Assert.Single(d.Ops);
        Assert.Equal(Red, op.Fill);
        Assert.Null(op.Stroke);
        var pts = op.Path.Where(s => s is MoveTo or LineTo).Select(s => s is MoveTo m ? m.P : ((LineTo)s).P).ToArray();
        Assert.Equal(3, pts.Length);
        Assert.Contains(pts, p => Math.Abs(p.X - 20) < Eps && Math.Abs(p.Y - 40) < Eps);   // apex: top centre (^)
        Assert.Contains(pts, p => Math.Abs(p.X - 10) < Eps && Math.Abs(p.Y - 20) < Eps);
        Assert.Contains(pts, p => Math.Abs(p.X - 30) < Eps && Math.Abs(p.Y - 20) < Eps);
        AssertWithin(d.Bounds, a.Rect, "caret");
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void AttachmentIcon_UprightForAllRotations(int rotation)
    {
        var rect = new PdfRectF(100, 200, 120, 220);
        var a = new Annotation { Kind = AnnotKind.FileAttachment, Rect = rect, Color = Red };
        var op = Assert.Single(AnnotationPainter.Paint(a, rotation).Ops);
        Assert.Equal(1.2f, op.StrokeWidth, 1e-4f);
        Assert.Equal(LineCap.Round, op.Cap);
        Assert.Equal(LineJoin.Round, op.Join);
        Assert.Null(op.Fill);
        Assert.Equal(Red, op.Stroke);

        var tf = new PageTransform(System.Drawing.RectangleF.FromLTRB(0, 0, 612, 792), rotation, 1);
        var view = op.Path.SelectMany(PointsOf).Select(p => tf.ToView(p.X, p.Y)).ToArray();
        var corners = new[] { tf.ToView(rect.Left, rect.Top), tf.ToView(rect.Right, rect.Bottom) };
        double left = corners.Min(c => c.X), top = corners.Min(c => c.Y);
        // Same upright clip for every rotation: u 6..14, v 1.5..18 measured from the icon's top-left on screen.
        Assert.Equal(left + 6, view.Min(v => v.X), 0.01);
        Assert.Equal(left + 14, view.Max(v => v.X), 0.01);
        Assert.Equal(top + 1.5, view.Min(v => v.Y), 0.01);
        Assert.Equal(top + 18, view.Max(v => v.Y), 0.01);
    }
}
