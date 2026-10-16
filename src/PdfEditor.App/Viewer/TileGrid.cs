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

    /// <summary>
    /// 타일(키의 배율 기준 픽셀)을 지금 배율의 문서 좌표(DIP) 사각형으로 바꾼다. scale = 1pt당 DIP.
    /// 쪽 원점은 장치 픽셀에 맞춰 둔다(안 맞으면 1:1로 그려도 보간되어 흐려진다).
    /// </summary>
    public static Rect DestRect(PageInfo info, TileKey key, Rect pageRect, double scale, double renderScaling)
    {
        var pb = key.PixelBounds(info);
        double k = scale / key.Scale;
        double ox = Math.Round(pageRect.X * renderScaling) / renderScaling, oy = Math.Round(pageRect.Y * renderScaling) / renderScaling;
        return new Rect(ox + pb.X * k, oy + pb.Y * k, pb.W * k, pb.H * k);
    }

    /// <summary>타일을 실제로 그릴 픽셀/포인트 = 1pt당 DIP × 화면 배율(RenderScaling).</summary>
    public static double PixelScale(double dipPerPoint, double renderScaling) => dipPerPoint * renderScaling;

    /// <param name="scale">1pt당 DIP(PageLayout.Scale). 문서 좌표 1단위 = 1 DIP.</param>
    /// <param name="pageRectDoc">문서 좌표(DIP)에서 이 페이지가 놓인 사각형.</param>
    /// <param name="renderScaling">화면 배율(Windows 125% = 1.25). 타일은 scale × renderScaling 픽셀/pt로 그린다.</param>
    public static IReadOnlyList<TileKey> Tiles(PageInfo page, double scale, Rect pageRectDoc, Rect viewportDoc, int ringTiles,
        double renderScaling = 1.0)
    {
        int sk = TileKey.ToScaleKey(PixelScale(scale, renderScaling));
        double s = sk / 1000.0;
        var (pw, ph) = PagePixels(page, s);
        if (pw <= 0 || ph <= 0) return Array.Empty<TileKey>();
        if (IsSingleTile(pw, ph)) return new[] { new TileKey(page.Id, page.Version, sk, 0, 0) };

        // 문서 좌표(DIP) → 이 페이지 기준 타일 픽셀
        double ring = Math.Max(0, ringTiles) * TileSize;
        double left = (viewportDoc.X - pageRectDoc.X) * renderScaling - ring, top = (viewportDoc.Y - pageRectDoc.Y) * renderScaling - ring;
        double right = (viewportDoc.Right - pageRectDoc.X) * renderScaling + ring, bottom = (viewportDoc.Bottom - pageRectDoc.Y) * renderScaling + ring;
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
