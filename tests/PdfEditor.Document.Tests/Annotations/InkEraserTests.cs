using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;
using Xunit;

namespace PdfEditor.Document.Tests.Annotations;

public class InkEraserTests
{
    private static ImmutableArray<ImmutableArray<PdfPointF>> S(params (float, float)[][] strokes)
        => [.. strokes.Select(s => ImmutableArray.CreateRange(s.Select(p => new PdfPointF(p.Item1, p.Item2))))];

    private static void Near(PdfPointF e, PdfPointF a)
    {
        Assert.Equal(e.X, a.X, 1e-3);
        Assert.Equal(e.Y, a.Y, 1e-3);
    }

    [Fact]
    public void CutMiddle_TwoPiecesEndingOnCircle()
    {
        var r = InkEraser.EraseCircle(S([(0, 0), (100, 0)]), new(50, 0), 10);
        Assert.Equal(2, r.Length);
        Assert.Equal(2, r[0].Length);
        Near(new(0, 0), r[0][0]); Near(new(40, 0), r[0][1]);
        Near(new(60, 0), r[1][0]); Near(new(100, 0), r[1][1]);
    }

    [Fact]
    public void CircleCoversStroke_StrokeRemoved()
    {
        var r = InkEraser.EraseCircle(S([(40, 0), (60, 0)]), new(50, 0), 30);
        Assert.Empty(r);
    }

    [Fact]
    public void CircleMisses_SameInstanceReturned()
    {
        var s = S([(0, 0), (100, 0)]);
        var r = InkEraser.EraseCircle(s, new(50, 50), 10);
        Assert.True(s.Equals(r));
    }

    [Fact]
    public void CutPolylineAtVertex_Works()
    {
        var r = InkEraser.EraseCircle(S([(0, 0), (50, 0), (50, 50)]), new(50, 0), 5);
        Assert.Equal(2, r.Length);
        Near(new(45, 0), r[0][^1]);
        Near(new(50, 5), r[1][0]);
        Near(new(50, 50), r[1][^1]);
    }

    [Fact]
    public void SinglePointStroke_InsideRemoved_OutsideKept()
    {
        var s = S([(10, 10)], [(100, 100)]);
        var r = InkEraser.EraseCircle(s, new(10, 12), 5);
        Assert.Single(r);
        Near(new(100, 100), r[0][0]);
    }

    [Fact]
    public void SamplePath_SpacingRespected()
    {
        var pts = InkEraser.SamplePath(new(0, 0), new(10, 0), 3f);
        Assert.Equal(new PdfPointF(0, 0), pts[0]);
        Assert.Equal(new PdfPointF(10, 0), pts[^1]);
        for (int i = 1; i < pts.Count; i++)
            Assert.True(Math.Abs(pts[i].X - pts[i - 1].X) <= 3f + 1e-4);
        Assert.Single(InkEraser.SamplePath(new(1, 1), new(1, 1), 3f));
    }
}
