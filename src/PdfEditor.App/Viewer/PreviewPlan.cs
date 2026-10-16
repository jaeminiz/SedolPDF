using Avalonia;
using PdfEditor.Document;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 첫 화면용 저해상도 미리보기(순수 계산). 큰 쪽(타일 여러 장)에서 보이는 영역을 낮은 배율로 PDFium 한 번에 그려 먼저 덮고,
/// 타일이 도착하면 그 위에 덮어 그린다. 미리보기는 타일 캐시에 음수 ScaleKey로 들어간다(캐시 한도 하나로 관리, FallbackFor는 건너뜀).
/// </summary>
public static class PreviewPlan
{
    /// <summary>미리보기 한 장의 최대 픽셀 수.</summary>
    public const double MaxPixels = 300_000;
    /// <summary>풀 배율 대비 최대 비율(절반 해상도 이하만 의미가 있다).</summary>
    public const double MaxFactor = 0.5;
    /// <summary>풀 배율 대비 최소 비율.</summary>
    public const double MinFactor = 0.125;
    /// <summary>보이는 타일 중 아직 없는 것이 이 수 이상일 때만 미리보기를 쓴다(1~2장이면 타일이 더 빠르다).</summary>
    public const int MinMissingTiles = 3;

    /// <summary>미리보기 배율(픽셀/pt)의 키. 영역 크기(pt)와 풀 배율(픽셀/pt)로 정한다.</summary>
    public static int ChooseScaleKey(double fullPxPerPt, double regionWPt, double regionHPt)
    {
        double area = Math.Max(1e-6, regionWPt * regionHPt * fullPxPerPt * fullPxPerPt);
        double factor = Math.Clamp(Math.Sqrt(MaxPixels / area), MinFactor, MaxFactor);
        return Math.Max(1, TileKey.ToScaleKey(fullPxPerPt * factor));
    }

    /// <summary>쪽 안의 보이는 영역(pt, 쪽 왼쪽 위 기준)을 미리보기 배율의 픽셀 사각형으로 바꾼다(바깥으로 키우고 쪽 끝에서 자른다). 비면 null.</summary>
    public static PixelRect? RegionPixels(PageInfo page, int scaleKey, Rect regionPt)
    {
        double s = scaleKey / 1000.0;
        var (pw, ph) = TileGrid.PagePixels(page, s);
        int x0 = Math.Clamp((int)Math.Floor(regionPt.X * s), 0, pw), y0 = Math.Clamp((int)Math.Floor(regionPt.Y * s), 0, ph);
        int x1 = Math.Clamp((int)Math.Ceiling(regionPt.Right * s), 0, pw), y1 = Math.Clamp((int)Math.Ceiling(regionPt.Bottom * s), 0, ph);
        return x1 > x0 && y1 > y0 ? new PixelRect(x0, y0, x1 - x0, y1 - y0) : null;
    }

    /// <summary>미리보기가 덮는 영역(pt).</summary>
    public static Rect CoveredPt(TileKey previewKey, int widthPx, int heightPx)
    {
        double s = previewKey.Scale;
        return new Rect(previewKey.X / s, previewKey.Y / s, widthPx / s, heightPx / s);
    }

    /// <summary>이미 있는 미리보기가 region(pt)을 다 덮는가. 픽셀 올림 오차는 1px만큼 허용한다.</summary>
    public static bool Covers(Rect coveredPt, double scale, Rect regionPt)
    {
        double tol = 1.0 / scale;
        return coveredPt.X <= regionPt.X + tol && coveredPt.Y <= regionPt.Y + tol
            && coveredPt.Right >= regionPt.Right - tol && coveredPt.Bottom >= regionPt.Bottom - tol;
    }

    public static TileKey KeyFor(PageInfo page, int scaleKey, PixelRect region) =>
        new(page.Id, page.Version, -scaleKey, region.X, region.Y);

    public static TileRequest RequestFor(PageInfo page, int scaleKey, PixelRect region) =>
        new(TileKind.Preview, KeyFor(page, scaleKey, region), scaleKey / 1000.0, region);

    /// <summary>미리보기를 그릴 문서 좌표(DIP) 사각형. scale = 1pt당 DIP.</summary>
    public static Rect DestRect(TileKey previewKey, int widthPx, int heightPx, Rect pageRect, double scale)
    {
        double k = scale / previewKey.Scale;
        return new Rect(pageRect.X + previewKey.X * k, pageRect.Y + previewKey.Y * k, widthPx * k, heightPx * k);
    }
}
