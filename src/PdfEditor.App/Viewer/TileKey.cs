using PdfEditor.Document;

namespace PdfEditor.App.Viewer;

public readonly record struct PixelRect(int X, int Y, int W, int H);

/// <summary>X, Y는 타일 격자 번호. 페이지 전체를 한 장으로 그리는 경우 (0, 0).</summary>
public readonly record struct TileKey(PageId Page, int Version, int ScaleKey, int X, int Y)
{
    public static int ToScaleKey(double scale) => (int)Math.Round(scale * 1000);
    public double Scale => Math.Abs(ScaleKey) / 1000.0;

    /// <summary>저해상도 미리보기 키(ScaleKey가 음수). X, Y는 미리보기 배율에서의 영역 왼쪽 위 픽셀. 일반 타일 조회·대체 그림 선택에서는 제외된다.</summary>
    public bool IsPreview => ScaleKey < 0;

    /// <summary>그 배율에서의 타일 픽셀 영역(페이지 왼쪽 위 기준). 페이지 끝에서 잘린다.</summary>
    public PixelRect PixelBounds(PageInfo page)
    {
        var (pw, ph) = TileGrid.PagePixels(page, Scale);
        if (TileGrid.IsSingleTile(pw, ph)) return new PixelRect(0, 0, pw, ph);
        int x = X * TileGrid.TileSize, y = Y * TileGrid.TileSize;
        return new PixelRect(x, y, Math.Max(0, Math.Min(TileGrid.TileSize, pw - x)), Math.Max(0, Math.Min(TileGrid.TileSize, ph - y)));
    }
}
