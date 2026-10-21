using Pdf.Native;

namespace PdfEditor.Document.Tests.Content;

public sealed class PdfPathGeometryTests
{
    private static PdfPathGeometry Square(bool fill, bool stroke, float sw = 1f) =>
        new([new PdfSubpath([new(0, 0), new(100, 0), new(100, 100), new(0, 100)], true)], fill, stroke, sw);

    [Fact]
    public void StrokeOnly_HitsOnlyNearLine()
    {
        var g = Square(false, true);
        Assert.True(g.Hit(new(50, 0), 0f));
        Assert.True(g.Hit(new(50, 1.4f), 1f));      // 허용 1 + 굵기/2 = 1.5
        Assert.False(g.Hit(new(50, 1.6f), 1f));
        Assert.False(g.Hit(new(50, 50), 4f));         // 속 빈 안쪽
        Assert.True(g.Hit(new(0, 50), 0f));           // 닫는 변
    }

    [Fact]
    public void Filled_HitsInside_AndHollowStrokedDoesNot()
    {
        Assert.True(Square(true, false).Hit(new(50, 50), 1f));
        Assert.False(Square(true, false).Hit(new(150, 50), 1f));
        Assert.True(Square(true, true).Hit(new(50, 50), 1f));
    }

    [Fact]
    public void Tolerance_ShrinksWhenZoomedIn()
    {
        var g = Square(false, true, 0f);
        var p = new PdfPointF(50, 3);
        Assert.True(g.Hit(p, 4f));    // 낮은 배율: 허용 오차 4pt
        Assert.False(g.Hit(p, 1f));   // 확대: 허용 오차 1pt
    }

    [Fact]
    public void OpenSubpath_IsNotClosed_ForStrokeOnly()
    {
        var g = new PdfPathGeometry([new PdfSubpath([new(0, 0), new(100, 0), new(100, 100)], false)], false, true, 0f);
        Assert.False(g.Hit(new(50, 50), 1f));   // 닫히지 않은 빗변 위
    }
}
