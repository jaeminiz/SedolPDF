using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Document.Tests.Annotations;

public class AnnotationRotateTests
{
    private const float Deg = MathF.PI / 180f;
    private const float Tol = 0.01f;

    private static void Near(PdfPointF e, PdfPointF a) { Assert.Equal(e.X, a.X, Tol); Assert.Equal(e.Y, a.Y, Tol); }
    private static void Near(PdfRectF e, PdfRectF a) { Assert.Equal(e.Left, a.Left, Tol); Assert.Equal(e.Bottom, a.Bottom, Tol); Assert.Equal(e.Right, a.Right, Tol); Assert.Equal(e.Top, a.Top, Tol); }

    private static Annotation Line(params PdfPointF[] v) => new() { Kind = AnnotKind.Line, Vertices = [.. v], BorderWidth = 0f, Rect = new PdfRectF(0, 0, 1, 1) };

    [Fact]
    public void Line_37deg_PointsRotated()
    {
        var r = AnnotationRotate.Apply(Line(new(0, 0), new(10, 0)), 37 * Deg, new(0, 0))!;
        Near(new(0, 0), r.Vertices[0]);
        Near(new(7.986f, 6.018f), r.Vertices[1]);
    }

    [Fact]
    public void Ink_90deg_RectRecomputed()
    {
        var a = new Annotation { Kind = AnnotKind.Ink, BorderWidth = 0f, Rect = new PdfRectF(0, 0, 1, 1), InkStrokes = [[new(10, 0), new(20, 0)], [new(10, 5), new(20, 5)]] };
        var r = AnnotationRotate.Apply(a, 90 * Deg, new(0, 0))!;
        Near(new(0, 10), r.InkStrokes[0][0]);
        Near(new(0, 20), r.InkStrokes[0][1]);
        Near(new(-5, 10), r.InkStrokes[1][0]);
        Assert.Equal(-5f, r.Rect.Left, 1.1f);   // 선 두께 여유(1pt)를 포함한 경계
        Assert.True(r.Rect.Top >= 20 && r.Rect.Right >= 0);
    }

    [Fact]
    public void Square_37deg_SnapsTo0_Unchanged()
    {
        var a = new Annotation { Kind = AnnotKind.Square, Rect = new PdfRectF(0, 0, 40, 10) };
        Near(a.Rect, AnnotationRotate.Apply(a, 37 * Deg, new(20, 5))!.Rect);
    }

    [Fact]
    public void Square_80deg_SnapsTo90_SwapsSizeKeepsCentre()
    {
        var a = new Annotation { Kind = AnnotKind.Square, Rect = new PdfRectF(0, 0, 40, 10) };
        var r = AnnotationRotate.Apply(a, 80 * Deg, new(20, 5))!;
        Near(new PdfRectF(15, -15, 25, 25), r.Rect);
        Assert.Equal(0, r.Rotation);   // 사각형은 Rotation 키가 없다
    }

    [Fact]
    public void Square_RotatedAboutOtherPoint_MovesCentre()
    {
        var a = new Annotation { Kind = AnnotKind.Square, Rect = new PdfRectF(10, -5, 30, 5) };   // 중심 (20,0), 20x10
        var r = AnnotationRotate.Apply(a, 90 * Deg, new(0, 0))!;
        Near(new PdfRectF(-5, 10, 5, 30), r.Rect);   // 중심 (0,20), 10x20
    }

    [Fact]
    public void FreeText_270deg_RotationAccumulates()
    {
        var a = new Annotation { Kind = AnnotKind.FreeText, Intent = FreeTextIntent.TextBox, Rect = new PdfRectF(0, 0, 40, 10), Rotation = 90 };
        Assert.Equal(0, AnnotationRotate.Apply(a, 270 * Deg, new(20, 5))!.Rotation);
        Assert.Equal(180, AnnotationRotate.Apply(a, 90 * Deg, new(20, 5))!.Rotation);
        Assert.Equal(0, AnnotationRotate.Apply(a, -90 * Deg, new(20, 5))!.Rotation);
    }

    [Fact]
    public void Stamp_90deg_AddsRotation()
    {
        var a = new Annotation { Kind = AnnotKind.Stamp, Rect = new PdfRectF(0, 0, 40, 10) };
        var r = AnnotationRotate.Apply(a, 90 * Deg, new(20, 5))!;
        Assert.Equal(90, r.Rotation);
        Near(new PdfRectF(15, -15, 25, 25), r.Rect);
    }

    [Fact]
    public void Callout_90deg_LeaderAndBoxMoveTogether()
    {
        var a = new Annotation
        {
            Kind = AnnotKind.FreeText, Intent = FreeTextIntent.Callout, BorderWidth = 0f,
            TextBox = new PdfRectF(0, 0, 40, 10), Rect = new PdfRectF(-30, 0, 40, 10),
            Vertices = [new(-30, 5), new(0, 5)],
        };
        var r = AnnotationRotate.Apply(a, 90 * Deg, new(20, 5))!;
        Near(new PdfRectF(15, -15, 25, 25), r.TextBox!.Value);
        Near(new(20, -45), r.Vertices[0]);   // (-30,5) → 중심 (20,5) 기준 반시계 90°
        Near(new(20, -15), r.Vertices[1]);
        Assert.Equal(90, r.Rotation);
        Assert.True(r.Rect.Bottom <= -45 && r.Rect.Top >= 25);   // Rect = 글 상자 + 지시선
    }

    [Theory]
    [InlineData(AnnotKind.Highlight)]
    [InlineData(AnnotKind.Underline)]
    [InlineData(AnnotKind.StrikeOut)]
    [InlineData(AnnotKind.Text)]
    [InlineData(AnnotKind.Caret)]
    [InlineData(AnnotKind.FileAttachment)]
    public void NonRotatable_ReturnsNull(AnnotKind kind)
    {
        var a = new Annotation { Kind = kind, Rect = new PdfRectF(0, 0, 10, 10) };
        Assert.False(AnnotationRotate.CanRotate(a));
        Assert.Null(AnnotationRotate.Apply(a, 90 * Deg, new(0, 0)));
    }

    [Fact]
    public void EffectiveAngle_Ties()
    {
        var sq = new Annotation { Kind = AnnotKind.Square };
        Assert.Equal(90 * Deg, AnnotationRotate.EffectiveAngle(sq, 45 * Deg), 1e-4f);
        Assert.Equal(-90 * Deg, AnnotationRotate.EffectiveAngle(sq, -45 * Deg), 1e-4f);
        Assert.Equal(0f, AnnotationRotate.EffectiveAngle(sq, 44 * Deg), 1e-4f);
        Assert.Equal(45 * Deg, AnnotationRotate.EffectiveAngle(Line(new(0, 0), new(1, 0)), 45 * Deg), 1e-6f);
    }

    [Fact]
    public void SnapsToQuarter_Kinds()
    {
        foreach (var k in new[] { AnnotKind.Square, AnnotKind.Circle, AnnotKind.FreeText, AnnotKind.Stamp })
            Assert.True(AnnotationRotate.SnapsToQuarter(new Annotation { Kind = k }));
        foreach (var k in new[] { AnnotKind.Line, AnnotKind.PolyLine, AnnotKind.Polygon, AnnotKind.Ink })
            Assert.False(AnnotationRotate.SnapsToQuarter(new Annotation { Kind = k }));
    }
}
