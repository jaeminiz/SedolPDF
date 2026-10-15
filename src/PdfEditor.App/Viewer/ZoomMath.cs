using Avalonia;

namespace PdfEditor.App.Viewer;

public static class ZoomMath
{
    public const double MinScale = 0.10;
    public const double MaxScale = 16.0;

    private static readonly double[] Steps = { 0.10, 0.25, 0.50, 0.75, 1.00, 1.25, 1.50, 2.00, 3.00, 4.00, 6.00, 8.00, 12.00, 16.00 };

    public static double Clamp(double scale) => Math.Clamp(scale, MinScale, MaxScale);

    /// <summary>direction &gt; 0: 지금보다 큰 다음 단계, &lt; 0: 작은 이전 단계. 범위 밖이면 끝값.</summary>
    public static double Step(double scale, int direction)
    {
        const double eps = 1e-9;
        if (direction > 0)
        {
            foreach (var s in Steps) if (s > scale + eps) return s;
            return MaxScale;
        }
        if (direction < 0)
        {
            for (int i = Steps.Length - 1; i >= 0; i--) if (Steps[i] < scale - eps) return Steps[i];
            return MinScale;
        }
        return Clamp(scale);
    }

    public static double FitWidth(Size pagePx1x, double viewportW) =>
        Clamp((viewportW - 2 * PageLayout.MarginPx) / pagePx1x.Width);

    public static double FitPage(Size pagePx1x, Size viewport) =>
        Clamp(Math.Min((viewport.Width - 2 * PageLayout.MarginPx) / pagePx1x.Width,
                       (viewport.Height - 2 * PageLayout.MarginPx) / pagePx1x.Height));

    /// <summary>
    /// 커서(anchorInViewport) 아래의 페이지 위 점이 배율 변경 뒤에도 같은 화면 위치에 오도록 새 스크롤 위치를 계산한다.
    /// 페이지 밖이면 가장 가까운 페이지 기준(페이지 크기 대비 비율로 외삽). viewport를 주면 [0, Extent-viewport]로,
    /// 안 주면 [0, Extent]로 제한한다.
    /// </summary>
    public static Vector AnchorOffset(PageLayout before, PageLayout after, Vector oldOffset, Point anchorInViewport, Size? viewport = null)
    {
        var doc = new Point(oldOffset.X + anchorInViewport.X, oldOffset.Y + anchorInViewport.Y);
        int page = before.HitTest(doc)?.Page ?? before.NearestPage(doc);
        double nx = oldOffset.X, ny = oldOffset.Y;
        if (page >= 0)
        {
            var rb = before.GetPageRect(page);
            var ra = after.GetPageRect(page);
            if (ra.Width > 0 && rb.Width > 0)
            {
                double u = (doc.X - rb.X) / rb.Width, v = (doc.Y - rb.Y) / rb.Height;
                nx = ra.X + u * ra.Width - anchorInViewport.X;
                ny = ra.Y + v * ra.Height - anchorInViewport.Y;
            }
        }
        double maxX = after.Extent.Width, maxY = after.Extent.Height;
        if (viewport is { } vp) { maxX = Math.Max(0, maxX - vp.Width); maxY = Math.Max(0, maxY - vp.Height); }
        return new Vector(Math.Clamp(nx, 0, maxX), Math.Clamp(ny, 0, maxY));
    }
}
