using Avalonia;
using PdfEditor.App.Viewer;

namespace PdfEditor.App.Tests.Viewer;

public class ViewportCoverageTests
{
    [Fact]
    public void Coverage_FullAndPartial()
    {
        var vp = new Rect(0, 0, 800, 800);
        var page = new Rect(100, 0, 400, 800);
        Assert.Equal(1.0, ViewportCoverage.Compute(vp, new[] { page }, new[] { page }), 3);
        Assert.Equal(0.5, ViewportCoverage.Compute(vp, new[] { page }, new[] { new Rect(100, 0, 400, 400) }), 2);
        Assert.Equal(0.0, ViewportCoverage.Compute(vp, new[] { page }, Array.Empty<Rect>()), 3);
    }

    [Fact]
    public void Coverage_AreaOutsidePagesIgnored_AndNoPagesIsOne()
    {
        var vp = new Rect(0, 0, 800, 800);
        var page = new Rect(0, 0, 800, 400);
        Assert.Equal(1.0, ViewportCoverage.Compute(vp, new[] { page }, new[] { new Rect(0, 0, 800, 400) }), 3);
        Assert.Equal(1.0, ViewportCoverage.Compute(vp, Array.Empty<Rect>(), Array.Empty<Rect>()), 3);
    }
}
