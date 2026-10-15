using Avalonia;
using PdfEditor.App.Viewer;

namespace PdfEditor.App.Tests.Viewer;

public class ZoomMathTests
{
    [Fact]
    public void Zoom_Step_UpDownAndClamp()
    {
        Assert.Equal(1.25, ZoomMath.Step(1.0, +1), 6);
        Assert.Equal(16.0, ZoomMath.Step(16, +1), 6);
        Assert.Equal(0.25, ZoomMath.Step(0.3, -1), 6);
        Assert.Equal(0.10, ZoomMath.Step(0.10, -1), 6);
        Assert.Equal(0.5, ZoomMath.Step(0.3, +1), 6);
        Assert.Equal(0.10, ZoomMath.Clamp(0.01), 6);
        Assert.Equal(16.0, ZoomMath.Clamp(50), 6);
    }

    [Fact]
    public void Zoom_AnchorOffset_KeepsPointUnderCursor()
    {
        var pages = VT.Pages(3);
        var before = PageLayout.Build(pages, 1.0, 1000, ViewMode.Continuous);
        var after = PageLayout.Build(pages, 2.0, 1000, ViewMode.Continuous);
        var oldOffset = new Vector(0, 900);
        var anchor = new Point(500, 300);
        var hit = before.HitTest(new Point(oldOffset.X + anchor.X, oldOffset.Y + anchor.Y));
        Assert.NotNull(hit);
        Assert.Equal(1, hit!.Value.Page);

        var newOffset = ZoomMath.AnchorOffset(before, after, oldOffset, anchor);
        var rect2 = after.GetPageRect(hit.Value.Page);
        var expectedDoc = new Point(rect2.X + hit.Value.InPage.X * 2, rect2.Y + hit.Value.InPage.Y * 2);
        Assert.Equal(anchor.X, expectedDoc.X - newOffset.X, 0.99);
        Assert.Equal(anchor.Y, expectedDoc.Y - newOffset.Y, 0.99);
        Assert.True(newOffset.X >= 0 && newOffset.Y >= 0);
        Assert.True(newOffset.Y <= after.Extent.Height);
    }

    [Fact]
    public void Zoom_AnchorOffset_ClampedIntoExtent()
    {
        var pages = VT.Pages(1);
        var before = PageLayout.Build(pages, 2.0, 1000, ViewMode.Continuous);
        var after = PageLayout.Build(pages, 0.5, 1000, ViewMode.Continuous);
        var o = ZoomMath.AnchorOffset(before, after, new Vector(300, 1200), new Point(10, 10), new Size(1000, 800));
        Assert.True(o.X >= 0 && o.Y >= 0);
        Assert.True(o.Y <= Math.Max(0, after.Extent.Height - 800));
    }

    [Fact]
    public void Zoom_FitWidth_FitPage()
    {
        var page = new Size(600, 800);
        Assert.Equal((1032 - 32) / 600.0, ZoomMath.FitWidth(page, 1032), 6);
        Assert.Equal((500 - 32) / 800.0, ZoomMath.FitPage(page, new Size(2000, 500)), 6);
        Assert.Equal((632 - 32) / 600.0, ZoomMath.FitPage(page, new Size(632, 5000)), 6);
        Assert.Equal(16.0, ZoomMath.FitWidth(new Size(1, 1), 100000), 6);
    }
}
