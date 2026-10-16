using Pdf.Native;
using PdfEditor.App.Viewer.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Annotations;

public class InkSimplifierTests
{
    [Fact]
    public void Simplify_CollinearPoints_KeepsEnds()
    {
        var pts = Enumerable.Range(0, 10).Select(i => new PdfPointF(i, i * 2)).ToList();
        var r = InkSimplifier.Simplify(pts, 0.5f);
        Assert.Equal([pts[0], pts[^1]], r);
    }

    [Fact]
    public void Simplify_KeepsCornerBeyondTolerance()
    {
        List<PdfPointF> pts = [new(0, 0), new(5, 0), new(10, 0), new(10, 5), new(10, 10)];
        var r = InkSimplifier.Simplify(pts, 0.5f);
        Assert.Equal([new PdfPointF(0, 0), new PdfPointF(10, 0), new PdfPointF(10, 10)], r);
    }
}
