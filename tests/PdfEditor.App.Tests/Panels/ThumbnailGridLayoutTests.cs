using Avalonia;
using PdfEditor.App.Panels;

namespace PdfEditor.App.Tests.Panels;

/// <summary>썸네일 여러 열 배치 계산(0.11.0).</summary>
public class ThumbnailGridLayoutTests
{
    [Theory]
    [InlineData(200, 174, 1)]
    [InlineData(348, 174, 2)]
    [InlineData(560, 174, 3)]
    [InlineData(100, 174, 1)]      // 칸보다 좁아도 한 열
    [InlineData(double.PositiveInfinity, 174, 1)]
    public void Columns_FitTheWidth(double width, double cell, int expected) => Assert.Equal(expected, ThumbnailGridLayout.Columns(width, cell));

    [Fact]
    public void Rows_RoundUp()
    {
        Assert.Equal(0, ThumbnailGridLayout.Rows(0, 3));
        Assert.Equal(1, ThumbnailGridLayout.Rows(3, 3));
        Assert.Equal(2, ThumbnailGridLayout.Rows(4, 3));
        Assert.Equal(100, ThumbnailGridLayout.Rows(100, 1));
    }

    [Fact]
    public void CellRect_PlacesLeftToRight_ThenNextRow_AndCentersLeftover()
    {
        // 폭 400, 칸 150×200 → 2열, 남는 100은 양쪽 50씩
        Assert.Equal(new Rect(50, 0, 150, 200), ThumbnailGridLayout.CellRect(0, 2, 400, 150, 200));
        Assert.Equal(new Rect(200, 0, 150, 200), ThumbnailGridLayout.CellRect(1, 2, 400, 150, 200));
        Assert.Equal(new Rect(50, 200, 150, 200), ThumbnailGridLayout.CellRect(2, 2, 400, 150, 200));
        Assert.Equal(new Rect(200, 400, 150, 200), ThumbnailGridLayout.CellRect(5, 2, 400, 150, 200));
    }

    [Fact]
    public void VisibleRange_Is_Virtualized_ForBigDocuments()
    {
        // 500쪽, 3열, 칸 높이 200, 화면 1000~1600 → 줄 5..8(+여유 1줄) 만 만든다
        var (first, last) = ThumbnailGridLayout.VisibleRange(1000, 1600, 200, 3, 500);
        Assert.Equal(4 * 3, first);
        Assert.Equal(10 * 3 - 1, last);
        Assert.True(last - first + 1 <= 30);
        // 맨 아래 가까이: 끝 칸까지만
        var (f2, l2) = ThumbnailGridLayout.VisibleRange(33000, 33600, 200, 3, 500);
        Assert.Equal(499, l2);
        Assert.True(f2 > 400);
        // 내용이 없으면 빈 범위
        var (f0, l0) = ThumbnailGridLayout.VisibleRange(0, 600, 200, 3, 0);
        Assert.True(l0 < f0);
    }

    [Fact]
    public void VisibleRange_ViewportPastEnd_StillKeepsLastRow()
    {
        var (first, last) = ThumbnailGridLayout.VisibleRange(99999, 100500, 200, 2, 7);
        Assert.Equal(7 - 1, last);
        Assert.True(first <= last);
    }

    [Fact]
    public void DropSlot_MultiColumn_LeftHalfIsBefore_RightHalfIsAfter()
    {
        // 폭 400, 칸 150×200, 2열(왼쪽 여백 50), 쪽 5개
        var before1 = ThumbnailGridLayout.DropSlotAt(new Point(60, 50), 5, 2, 400, 150, 200);   // 0번 칸 왼쪽 절반
        Assert.Equal(0, before1.Index);
        Assert.Equal(50 - 1.5, before1.Caret.X, 3);
        Assert.Equal(200, before1.Caret.Height);   // 세로선

        var after0 = ThumbnailGridLayout.DropSlotAt(new Point(180, 50), 5, 2, 400, 150, 200);   // 0번 칸 오른쪽 절반
        Assert.Equal(1, after0.Index);

        var before3 = ThumbnailGridLayout.DropSlotAt(new Point(210, 250), 5, 2, 400, 150, 200);   // 3번 칸(2행 오른쪽) 왼쪽 절반
        Assert.Equal(3, before3.Index);
        Assert.Equal(200, before3.Caret.Y);
    }

    [Fact]
    public void DropSlot_AfterLastInRow_ShowsCaretAtThatCellsRightEdge()
    {
        var s = ThumbnailGridLayout.DropSlotAt(new Point(340, 50), 5, 2, 400, 150, 200);   // 1번 칸 오른쪽 절반
        Assert.Equal(2, s.Index);
        Assert.Equal(350 - 1.5, s.Caret.X, 3);   // 다음 줄 앞이 아니라 같은 줄 오른쪽 끝에 보인다
        Assert.Equal(0, s.Caret.Y);
    }

    [Fact]
    public void DropSlot_EmptySpace_MeansEnd_AndAboveTopMeansStart()
    {
        // 쪽 5개, 2열: 3행의 오른쪽 칸(5번)은 비어 있다
        var end = ThumbnailGridLayout.DropSlotAt(new Point(300, 450), 5, 2, 400, 150, 200);
        Assert.Equal(5, end.Index);
        var below = ThumbnailGridLayout.DropSlotAt(new Point(100, 9000), 5, 2, 400, 150, 200);
        Assert.Equal(5, below.Index);
        var above = ThumbnailGridLayout.DropSlotAt(new Point(60, -20), 5, 2, 400, 150, 200);
        Assert.Equal(0, above.Index);
    }

    [Fact]
    public void DropSlot_SingleColumn_UsesTopAndBottomHalves_WithHorizontalLine()
    {
        var upper = ThumbnailGridLayout.DropSlotAt(new Point(100, 210), 4, 1, 200, 174, 200);   // 1번 칸 위쪽 절반
        Assert.Equal(1, upper.Index);
        Assert.Equal(200, upper.Caret.Width);   // 가로선
        var lower = ThumbnailGridLayout.DropSlotAt(new Point(100, 390), 4, 1, 200, 174, 200);   // 1번 칸 아래쪽 절반
        Assert.Equal(2, lower.Index);
        Assert.Equal(400 - 1.5, lower.Caret.Y, 3);
    }

    [Fact]
    public void DropSlot_EmptyDocument_IsZero() => Assert.Equal(0, ThumbnailGridLayout.DropSlotAt(new Point(10, 10), 0, 2, 400, 150, 200).Index);
}
