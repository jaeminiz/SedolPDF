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

    private static Annotation LineAnn(LineEnding end = LineEnding.None) => new()
    {
        Kind = AnnotKind.Line, BorderWidth = 2f, EndEnding = end,
        Vertices = [new PdfPointF(10, 10), new PdfPointF(110, 10)],
    };

    [Fact]
    public void ComputeRect_LineWithArrow_IncludesArrowHead()
    {
        var plain = AnnotationGeometry.ComputeRect(LineAnn());
        var arrow = AnnotationGeometry.ComputeRect(LineAnn(LineEnding.OpenArrow));
        Assert.Equal(new PdfRectF(9, 9, 111, 11), plain);
        Assert.True(arrow.Right >= 110 + 6f && arrow.Top >= 10 + 6f && arrow.Left <= 4 && arrow.Bottom <= 4);
    }

    [Fact]
    public void Translate_CalloutMovesBoxAndLeader()
    {
        var a = new Annotation
        {
            Kind = AnnotKind.FreeText, Intent = FreeTextIntent.Callout, BorderWidth = 1f,
            TextBox = new PdfRectF(100, 100, 200, 150),
            Vertices = [new PdfPointF(50, 50), new PdfPointF(100, 110)],
        };
        a = a with { Rect = AnnotationGeometry.ComputeRect(a) };
        var t = AnnotationGeometry.Translate(a, 5, -3);
        Assert.Equal(new PdfRectF(105, 97, 205, 147), t.TextBox);
        Assert.Equal(new PdfPointF(55, 47), t.Vertices[0]);
        Assert.Equal(new PdfPointF(105, 107), t.Vertices[1]);
        Assert.Equal(a.Rect.Offset(5, -3), t.Rect);
    }

    [Fact]
    public void Resize_PolygonScalesVertices()
    {
        var a = new Annotation
        {
            Kind = AnnotKind.Polygon, BorderWidth = 2f,
            Vertices = [new PdfPointF(0, 0), new PdfPointF(10, 0), new PdfPointF(10, 10), new PdfPointF(0, 10)],
        };
        a = a with { Rect = AnnotationGeometry.ComputeRect(a) };
        var r = AnnotationGeometry.Resize(a, new PdfRectF(-1, -1, 21, 11));
        Assert.Equal(new PdfPointF(0, 0), r.Vertices[0]);
        Assert.Equal(new PdfPointF(20, 0), r.Vertices[1]);
        Assert.Equal(new PdfPointF(20, 10), r.Vertices[2]);
        Assert.Equal(new PdfRectF(-1, -1, 21, 11), r.Rect);
    }

    [Fact]
    public void Resize_FreeText_ChangesBoxOnly()
    {
        var a = new Annotation { Kind = AnnotKind.FreeText, Rect = new PdfRectF(0, 0, 100, 50), FontSizePt = 14f };
        var r = AnnotationGeometry.Resize(a, new PdfRectF(0, 0, 200, 80));
        Assert.Equal(new PdfRectF(0, 0, 200, 80), r.Rect);
        Assert.Equal(14f, r.FontSizePt);

        var c = a with { Intent = FreeTextIntent.Callout, TextBox = a.Rect, Vertices = [new PdfPointF(-40, -40), new PdfPointF(0, 10)] };
        var rc = AnnotationGeometry.Resize(c, new PdfRectF(0, 0, 150, 60));
        Assert.Equal(new PdfRectF(0, 0, 150, 60), rc.TextBox);
        Assert.Equal(c.Vertices, rc.Vertices);
    }

    [Theory]
    [InlineData(AnnotKind.Line)]
    [InlineData(AnnotKind.PolyLine)]
    [InlineData(AnnotKind.Polygon)]
    [InlineData(AnnotKind.FreeText)]
    [InlineData(AnnotKind.Stamp)]
    public void CanResize_NewKinds(AnnotKind k) => Assert.True(AnnotationGeometry.CanResize(k));

    [Theory]
    [InlineData(AnnotKind.Caret)]
    [InlineData(AnnotKind.FileAttachment)]
    public void CannotResize_CaretAndAttachment(AnnotKind k) => Assert.False(AnnotationGeometry.CanResize(k));

    [Fact]
    public void Translate_LineStampCaretAttachment()
    {
        var line = AnnotationGeometry.Translate(LineAnn(), 1, 2);
        Assert.Equal(new PdfPointF(11, 12), line.Vertices[0]);
        foreach (var k in new[] { AnnotKind.Stamp, AnnotKind.Caret, AnnotKind.FileAttachment })
        {
            var t = AnnotationGeometry.Translate(new Annotation { Kind = k, Rect = new PdfRectF(0, 0, 20, 20) }, 3, 4);
            Assert.Equal(new PdfRectF(3, 4, 23, 24), t.Rect);
        }
    }

    [Fact]
    public void ComputeRect_CloudPolygon_AddsRd()
    {
        var verts = ImmutableArray.Create(new PdfPointF(0, 0), new PdfPointF(10, 0), new PdfPointF(5, 10));
        var plain = AnnotationGeometry.ComputeRect(new Annotation { Kind = AnnotKind.Polygon, BorderWidth = 2f, Vertices = verts });
        var cloud = AnnotationGeometry.ComputeRect(new Annotation { Kind = AnnotKind.Polygon, BorderWidth = 2f, Vertices = verts, CloudIntensity = 1f });
        Assert.True(cloud.Width > plain.Width);
        Assert.Equal(7f, 0 - cloud.Left, 3);   // bw/2 + d/2 + bw/2, d = 10
    }

    [Fact]
    public void IsPatchKind_ExactlySevenNewKinds()
    {
        var patch = Enum.GetValues<AnnotKind>().Where(k => k.IsPatchKind()).ToHashSet();
        Assert.Equal(7, patch.Count);
        Assert.DoesNotContain(AnnotKind.Ink, patch);
        Assert.DoesNotContain(AnnotKind.Circle, patch);
    }

    [Fact]
    public void LineEnding_PdfNames_RoundTrip_UnknownIsNull()
    {
        foreach (var e in Enum.GetValues<LineEnding>())
            Assert.Equal(e, LineEndingExtensions.FromPdfName(e.ToPdfName()));
        Assert.Null(LineEndingExtensions.FromPdfName("ROpenArrow"));
        Assert.Equal(LineEnding.OpenArrow, LineEndingExtensions.FromPdfName("/OpenArrow"));
    }

    [Fact]
    public void Comparer_ComparesNewFields()
    {
        var a = new Annotation
        {
            Kind = AnnotKind.FreeText, TextBox = new PdfRectF(0, 0, 10, 10),
            Vertices = [new PdfPointF(1, 1), new PdfPointF(2, 2)],
            Stamp = new StampSpec(StampKind.Text, "", "x", null),
        };
        var same = a with { Vertices = [new PdfPointF(1, 1), new PdfPointF(2, 2)], Stamp = new StampSpec(StampKind.Text, "", "x", null) };
        Assert.True(AnnotationComparer.Equivalent(a, same));
        Assert.False(AnnotationComparer.Equivalent(a, a with { Vertices = [new PdfPointF(1, 1)] }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { Vertices = [new PdfPointF(1, 1), new PdfPointF(2, 3)] }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { TextBox = new PdfRectF(0, 0, 10, 11) }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { TextBox = null }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { EndEnding = LineEnding.OpenArrow }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { StartEnding = LineEnding.Circle }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { CloudIntensity = 1f }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { Intent = FreeTextIntent.Callout }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { FontSizePt = 20f }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { TextColor = new PdfColor(1, 2, 3) }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { Align = TextAlign.Center }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { InReplyTo = new AnnotId(5) }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { ReplyKind = ReplyType.Group }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { Stamp = new StampSpec(StampKind.Text, "", "y", null) }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { AttachmentId = "i" }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { AttachmentName = "n" }));
    }
}
