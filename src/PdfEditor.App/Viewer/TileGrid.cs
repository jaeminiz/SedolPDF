using Avalonia;
using PdfEditor.Document;

namespace PdfEditor.App.Viewer;

public static class TileGrid
{
    public const int TileSize = 512;
    public const int SingleTileMax = 1024;

    internal static (int W, int H) PagePixels(PageInfo page, double scale) =>
        ((int)Math.Ceiling(page.WidthPt * scale - 1e-6), (int)Math.Ceiling(page.HeightPt * scale - 1e-6));

    internal static bool IsSingleTile(int pw, int ph) => pw <= SingleTileMax && ph <= SingleTileMax;

    /// <param name="scale">화면 픽셀/포인트. 문서 좌표 1단위 = 화면 1픽셀.</param>
    /// <param name="pageRectDoc">문서 좌표에서 이 페이지가 놓인 사각형.</param>
    public static IReadOnlyList<TileKey> Tiles(PageInfo page, double scale, Rect pageRectDoc, Rect viewportDoc, int ringTiles)
    {
        int sk = TileKey.ToScaleKey(scale);
        double s = sk / 1000.0;
        var (pw, ph) = PagePixels(page, s);
        if (pw <= 0 || ph <= 0) return Array.Empty<TileKey>();
        if (IsSingleTile(pw, ph)) return new[] { new TileKey(page.Id, page.Version, sk, 0, 0) };

        double ring = Math.Max(0, ringTiles) * TileSize;
        double left = viewportDoc.X - ring - pageRectDoc.X, top = viewportDoc.Y - ring - pageRectDoc.Y;
        double right = viewportDoc.Right + ring - pageRectDoc.X, bottom = viewportDoc.Bottom + ring - pageRectDoc.Y;
        left = Math.Max(left, 0); top = Math.Max(top, 0);
        right = Math.Min(right, pw); bottom = Math.Min(bottom, ph);
        if (right <= left || bottom <= top) return Array.Empty<TileKey>();

        int x0 = (int)Math.Floor(left / TileSize), x1 = (int)Math.Ceiling(right / TileSize) - 1;
        int y0 = (int)Math.Floor(top / TileSize), y1 = (int)Math.Ceiling(bottom / TileSize) - 1;
        var res = new List<TileKey>((x1 - x0 + 1) * (y1 - y0 + 1));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                res.Add(new TileKey(page.Id, page.Version, sk, x, y));
        return res;
    }
}
