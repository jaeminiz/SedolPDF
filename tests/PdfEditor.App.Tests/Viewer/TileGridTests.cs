using PixelRect = PdfEditor.App.Viewer.PixelRect;
using Avalonia;
using PdfEditor.App.Viewer;

namespace PdfEditor.App.Tests.Viewer;

public class TileGridTests
{
    [Fact]
    public void Grid_SmallPage_SingleTile()
    {
        var p = VT.P(1);
        var tiles = TileGrid.Tiles(p, 1.0, new Rect(100, 16, 595, 842), new Rect(0, 0, 1000, 800), 1);
        var t = Assert.Single(tiles);
        Assert.Equal((0, 0), (t.X, t.Y));
        Assert.Equal(1000, t.ScaleKey);
        Assert.Equal(new PixelRect(0, 0, 595, 842), t.PixelBounds(p));
    }

    [Fact]
    public void Grid_A1_At1x_ViewportPlusRing()
    {
        var p = VT.P(1, 1684, 2384);
        var tiles = TileGrid.Tiles(p, 1.0, new Rect(0, 0, 1684, 2384), new Rect(0, 0, 1000, 800), 1);
        Assert.Equal(9, tiles.Count);
        Assert.Equal(9, tiles.Select(t => (t.X, t.Y)).Distinct().Count());
        Assert.All(tiles, t => { Assert.InRange(t.X, 0, 2); Assert.InRange(t.Y, 0, 2); });
        Assert.Equal(4, TileGrid.Tiles(p, 1.0, new Rect(0, 0, 1684, 2384), new Rect(0, 0, 1000, 800), 0).Count);
    }

    [Fact]
    public void Grid_EdgeTiles_ClippedToPage()
    {
        var p = VT.P(1, 1684, 2384);
        var tiles = TileGrid.Tiles(p, 1.0, new Rect(0, 0, 1684, 2384), new Rect(1600, 2300, 200, 200), 0);
        var t = Assert.Single(tiles);
        Assert.Equal((3, 4), (t.X, t.Y));
        Assert.Equal(new PixelRect(1536, 2048, 1684 - 1536, 2384 - 2048), t.PixelBounds(p));
    }

    [Fact]
    public void Grid_ViewportOutsidePage_NoTiles()
    {
        var p = VT.P(1, 1684, 2384);
        Assert.Empty(TileGrid.Tiles(p, 1.0, new Rect(0, 0, 1684, 2384), new Rect(0, 5000, 100, 100), 0));
    }

    [Fact]
    public void Grid_PageOffsetInDoc_IsRespected()
    {
        var p = VT.P(1, 1684, 2384);
        var tiles = TileGrid.Tiles(p, 1.0, new Rect(100, 1000, 1684, 2384), new Rect(100, 1000, 100, 100), 0);
        var t = Assert.Single(tiles);
        Assert.Equal((0, 0), (t.X, t.Y));
    }

    [Fact]
    public void Grid_RenderScaling1_5_TilePixelSizeIsDipTimesScaling()
    {
        // A4 실제 크기(100%) = 1.3333 DIP/pt, 화면 배율 150% → 2 픽셀/pt: 쪽 1190×1684 px, 타일 여러 장
        var p = VT.P(1, 595, 842);
        double dip = ZoomMath.DipPerPoint(1.0);
        var pageRect = new Rect(0, 0, 595 * dip, 842 * dip);
        var tiles = TileGrid.Tiles(p, dip, pageRect, new Rect(0, 0, 794, 600), 0, 1.5);
        Assert.All(tiles, t => Assert.Equal(2000, t.ScaleKey));
        // 뷰포트 794×600 DIP = 1191×900 px → 타일 3×2
        Assert.Equal(6, tiles.Count);
        Assert.Equal(new PixelRect(0, 0, 512, 512), tiles.Single(t => (t.X, t.Y) == (0, 0)).PixelBounds(p));
        Assert.Equal(new PixelRect(1024, 512, 166, 512), tiles.Single(t => (t.X, t.Y) == (2, 1)).PixelBounds(p));
    }

    [Fact]
    public void Grid_SmallPage_RenderScaling1_5_SingleTileHasScaledPixels()
    {
        var p = VT.P(1, 300, 400);   // 100%: 400×533 DIP → 1.5배면 600×800 px (한 장)
        var tiles = TileGrid.Tiles(p, ZoomMath.DipPerPoint(1.0), new Rect(0, 0, 400, 533.33), new Rect(0, 0, 1000, 800), 1, 1.5);
        var t = Assert.Single(tiles);
        Assert.Equal(new PixelRect(0, 0, 600, 800), t.PixelBounds(p));
    }

    [Fact]
    public void DestRect_FullScaleTile_IsPixelSizeOverRenderScaling_AndSnapped()
    {
        var p = VT.P(1, 300, 400);
        double dip = ZoomMath.DipPerPoint(1.0);
        var key = TileGrid.Tiles(p, dip, new Rect(0, 0, 400, 533), new Rect(0, 0, 1000, 800), 0, 1.5).Single();
        var d = TileGrid.DestRect(p, key, new Rect(100.2, 16, 400, 533.33), dip, 1.5);
        Assert.Equal(600 / 1.5, d.Width, 2);
        Assert.Equal(800 / 1.5, d.Height, 2);
        Assert.Equal(0, d.X * 1.5 - Math.Round(d.X * 1.5), 6);   // 장치 픽셀 경계
    }
}
