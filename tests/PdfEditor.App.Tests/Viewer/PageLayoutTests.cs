using Avalonia;
using PdfEditor.App.Viewer;
using PdfEditor.Document;

namespace PdfEditor.App.Tests.Viewer;

internal static class VT
{
    public static PageInfo P(long id, float w = 595, float h = 842, int v = 1) => new(new PageId(id), v, w, h);
    public static List<PageInfo> Pages(int n, float w = 595, float h = 842) => Enumerable.Range(0, n).Select(i => P(i, w, h)).ToList();
}

public class PageLayoutTests
{
    [Fact]
    public void Layout_Continuous_StacksWithGapAndCenters()
    {
        var l = PageLayout.Build(VT.Pages(2), 1.0, 1000, ViewMode.Continuous);
        var r0 = l.GetPageRect(0); var r1 = l.GetPageRect(1);
        Assert.Equal(16, r0.Y, 3);
        Assert.Equal(16 + 842 + 12, r1.Y, 3);
        Assert.Equal((1000 - 595) / 2.0, r0.X, 3);
        Assert.Equal(r0.X, r1.X, 3);
        Assert.Equal(16 + 842 + 12 + 842 + 16, l.Extent.Height, 3);
        Assert.Equal(1000, l.Extent.Width, 3);
    }

    [Fact]
    public void Layout_WiderThanViewport_UsesMargin()
    {
        var l = PageLayout.Build(VT.Pages(1), 2.0, 500, ViewMode.Continuous);
        Assert.Equal(16, l.GetPageRect(0).X, 3);
        Assert.Equal(595 * 2 + 32, l.Extent.Width, 3);
    }

    [Fact]
    public void Layout_CurrentPage_LargestVisibleHeight()
    {
        var l = PageLayout.Build(VT.Pages(3), 1.0, 1000, ViewMode.Continuous);
        // page0: 16..858, page1: 870..1712. Viewport 600..1300 -> p0 visible 258, p1 visible 430
        Assert.Equal(1, l.CurrentPage(new Rect(0, 600, 1000, 700)));
        Assert.Equal(0, l.CurrentPage(new Rect(0, 0, 1000, 700)));
        // tie -> earlier page: viewport symmetric around the gap (864)
        Assert.Equal(0, l.CurrentPage(new Rect(0, 864 - 100, 1000, 200)));
        Assert.Equal(new[] { 0, 1 }, l.PagesIntersecting(new Rect(0, 600, 1000, 700)));
    }

    [Fact]
    public void Layout_SinglePage_OnlyThatPage()
    {
        var l = PageLayout.Build(VT.Pages(3), 1.0, 1000, ViewMode.SinglePage, 1);
        Assert.Equal(0, l.GetPageRect(0).Width);
        Assert.Equal(0, l.GetPageRect(2).Height);
        Assert.Equal(16, l.GetPageRect(1).Y, 3);
        Assert.Equal(842 + 32, l.Extent.Height, 3);
        Assert.Equal(new[] { 1 }, l.PagesIntersecting(new Rect(0, 0, 1000, 5000)));
        Assert.Equal(1, l.CurrentPage(new Rect(0, 0, 1000, 500)));
    }

    [Fact]
    public void Layout_HitTest_ReturnsPageAndPoint()
    {
        var l = PageLayout.Build(VT.Pages(2), 1.0, 1000, ViewMode.Continuous);
        var r1 = l.GetPageRect(1);
        var hit = l.HitTest(new Point(r1.X + 10, r1.Y + 20));
        Assert.NotNull(hit);
        Assert.Equal(1, hit!.Value.Page);
        Assert.Equal(10, hit.Value.InPage.X, 3);
        Assert.Equal(20, hit.Value.InPage.Y, 3);
        Assert.Null(l.HitTest(new Point(2, 2)));
        Assert.Null(l.HitTest(new Point(500, 16 + 842 + 5))); // gap
    }
}
