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
}
