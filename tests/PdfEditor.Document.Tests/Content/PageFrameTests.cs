using Pdf.Native;
using PdfEditor.Document.Content;

namespace PdfEditor.Document.Tests.Content;

/// <summary>0.9.0 손 시험 후속: 회전이 다른 쪽으로 붙여도 보이는 자리·방향이 같다. 방향키는 화면 기준.</summary>
public sealed class PageFrameTests
{
    private static PdfPointF View(int rot, PdfRectF box, PdfPointF p) =>
        PageFrame.ToView(rot, box.Width, box.Height).Apply(new PdfPointF(p.X - box.Left, p.Y - box.Bottom));

    [Theory]
    [InlineData(1, 3)]
    [InlineData(0, 1)]
    [InlineData(3, 0)]
    [InlineData(2, 1)]
    [InlineData(1, 1)]
    public void Between_KeepsVisualPosition(int src, int dst)
    {
        var sb = new PdfRectF(0, 0, 841.92f, 595.32f);
        var db = new PdfRectF(0, 0, 841.92f, 595.32f);
        var p1 = new PdfPointF(120f, 400f);
        var p2 = PageFrame.Between(src, sb, dst, db).Apply(p1);
        var a = View(src, sb, p1); var b = View(dst, db, p2);
        Assert.InRange(a.X - b.X, -0.01f, 0.01f);
        Assert.InRange(a.Y - b.Y, -0.01f, 0.01f);
    }

    [Fact]
    public void Between_OffsetBoxes_KeepsVisualPosition()
    {
        var sb = new PdfRectF(10, 20, 600, 800);
        var db = new PdfRectF(-5, 7, 595, 799);
        var p1 = new PdfPointF(100f, 300f);
        var p2 = PageFrame.Between(1, sb, 3, db).Apply(p1);
        var a = View(1, sb, p1); var b = View(3, db, p2);
        Assert.InRange(a.X - b.X, -0.01f, 0.01f);
        Assert.InRange(a.Y - b.Y, -0.01f, 0.01f);
    }

    [Fact]
    public void Place_PullsIntoTargetPage()
    {
        var box = new PdfRectF(0, 0, 100, 100);
        var m = PageFrame.Place(0, box, 0, box, new PdfRectF(90, 90, 130, 130));
        var q = m.Apply(new PdfPointF(130, 130));
        Assert.True(q.X <= 100.01f && q.Y <= 100.01f);
    }

    [Theory]
    [InlineData(0, 1f, 0f, 1f, 0f)]
    [InlineData(1, 1f, 0f, 0f, 1f)]    // 90° 쪽: 화면 오른쪽 = 쪽 위쪽
    [InlineData(2, 1f, 0f, -1f, 0f)]
    [InlineData(3, 1f, 0f, 0f, -1f)]
    public void ScreenDirection_Right(int rot, float right, float up, float ex, float ey)
    {
        var (dx, dy) = ScreenDirection.ToPage(rot, right, up);
        Assert.Equal(ex, dx); Assert.Equal(ey, dy);
    }

    [Fact]
    public void ScreenDirection_Down_OnRotated90_IsPagePlusX()
    {
        var (dx, dy) = ScreenDirection.ToPage(1, 0f, -1f);   // 화면 아래
        Assert.Equal(1f, dx); Assert.Equal(0f, dy);
    }
}
