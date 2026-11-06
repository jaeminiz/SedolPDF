using Avalonia;
using PdfEditor.App.Hosting;
using Xunit;

namespace PdfEditor.App.Tests.Hosting;

/// <summary>0.8.0 단계 4: 탭을 끌다 놓을 곳 판정(화면 물리 픽셀 좌표).</summary>
public class DropTargetFinderTests
{
    private static readonly object A = "A", B = "B";
    private static readonly IReadOnlyList<(double, double)> ThreeTabs = [(0, 100), (100, 100), (200, 100)];

    // 탭 줄: 왼쪽 위 (x, y), 폭 600, 높이 32(물리 픽셀 = DIP × scaling)
    private static StripInfo Strip(object w, int x, int y, double scaling = 1.0)
        => new(w, new PixelRect(x, y, (int)(600 * scaling), (int)(32 * scaling)), ThreeTabs, scaling);

    [Fact]
    public void InsideOwnStrip_IsReorder()
    {
        var t = DropTargetFinder.Find(new PixelPoint(160, 110), A, [Strip(A, 0, 100), Strip(B, 1000, 100)], 3);
        Assert.Equal(new DropTarget(DropKind.Reorder, A, 2), t);
    }

    [Fact]
    public void InsideOtherStrip_IsOtherWindowWithSlot()
    {
        var t = DropTargetFinder.Find(new PixelPoint(1040, 110), A, [Strip(A, 0, 100), Strip(B, 1000, 100)], 3);
        Assert.Equal(new DropTarget(DropKind.OtherWindow, B, 0), t);
        t = DropTargetFinder.Find(new PixelPoint(1500, 110), A, [Strip(A, 0, 100), Strip(B, 1000, 100)], 3);
        Assert.Equal(new DropTarget(DropKind.OtherWindow, B, 3), t);   // 탭 오른쪽 빈 곳 = 맨 끝
    }

    [Fact]
    public void JustBelowStrip_WithinTearOff_StaysReorder()
    {
        // 탭 줄 아래 끝(132)에서 20 아래: 여유(32) 안
        var t = DropTargetFinder.Find(new PixelPoint(50, 152), A, [Strip(A, 0, 100)], 3);
        Assert.Equal(DropKind.Reorder, t.Kind);
        t = DropTargetFinder.Find(new PixelPoint(50, 75), A, [Strip(A, 0, 100)], 3);   // 위로 25
        Assert.Equal(DropKind.Reorder, t.Kind);
    }

    [Fact]
    public void FarBelowStrip_IsNewWindow()
    {
        var t = DropTargetFinder.Find(new PixelPoint(50, 400), A, [Strip(A, 0, 100), Strip(B, 1000, 100)], 3);
        Assert.Equal(new DropTarget(DropKind.NewWindow, null, -1), t);
    }

    [Fact]
    public void FarBelowStrip_SingleTab_IsNone()
    {
        var t = DropTargetFinder.Find(new PixelPoint(50, 400), A, [Strip(A, 0, 100)], 1);
        Assert.Equal(DropKind.None, t.Kind);
        // 탭 하나여도 다른 창 탭 줄에는 놓을 수 있다
        t = DropTargetFinder.Find(new PixelPoint(1040, 110), A, [Strip(A, 0, 100), Strip(B, 1000, 100)], 1);
        Assert.Equal(DropKind.OtherWindow, t.Kind);
    }

    [Fact]
    public void OverlappingStrips_FrontWindowWins()
    {
        // B가 더 최근 활성(앞)이고 A의 탭 줄 위에 겹쳐 있다
        var t = DropTargetFinder.Find(new PixelPoint(140, 110), A, [Strip(B, 100, 100), Strip(A, 0, 100)], 3);
        Assert.Equal(new DropTarget(DropKind.OtherWindow, B, 0), t);
        t = DropTargetFinder.Find(new PixelPoint(140, 110), A, [Strip(A, 0, 100), Strip(B, 100, 100)], 3);
        Assert.Equal(DropKind.Reorder, t.Kind);
    }

    [Fact]
    public void FrontWindowBody_HidesStripOfWindowBehind()
    {
        // 앞 창 B(본문 0..800 × 300..900)가 뒤 창 A의 탭 줄(y 400)을 덮고 있다: 그 위에 놓으면 A로 가지 않는다
        var front = new StripInfo(B, new PixelRect(0, 300, 600, 32), ThreeTabs, 1.0, new PixelRect(0, 300, 800, 600));
        var behind = new StripInfo(A, new PixelRect(0, 400, 600, 32), ThreeTabs, 1.0, new PixelRect(0, 400, 800, 600));
        var t = DropTargetFinder.Find(new PixelPoint(50, 410), B, [front, behind], 3);
        Assert.Equal(DropKind.NewWindow, t.Kind);
        // 앞 창 밖이면 뒤 창 탭 줄에 놓인다
        behind = behind with { StripRect = new PixelRect(1000, 400, 600, 32), WindowRect = new PixelRect(1000, 400, 800, 600) };
        Assert.Equal(DropKind.OtherWindow, DropTargetFinder.Find(new PixelPoint(1050, 410), B, [front, behind], 3).Kind);
    }

    [Fact]
    public void Scaling150_SlotUsesDip()
    {
        // 물리 x 300 → DIP 200: 세 번째 탭(200..300)의 가운데보다 왼쪽 → 자리 2
        var t = DropTargetFinder.Find(new PixelPoint(300, 110), A, [Strip(A, 0, 100, 1.5)], 3);
        Assert.Equal(new DropTarget(DropKind.Reorder, A, 2), t);
        // 여유도 물리 픽셀로 1.5배: 탭 줄 아래 끝(148)에서 40 아래는 여유(48) 안
        Assert.Equal(DropKind.Reorder, DropTargetFinder.Find(new PixelPoint(10, 188), A, [Strip(A, 0, 100, 1.5)], 3).Kind);
        Assert.Equal(DropKind.NewWindow, DropTargetFinder.Find(new PixelPoint(10, 200), A, [Strip(A, 0, 100, 1.5)], 3).Kind);
    }
}
