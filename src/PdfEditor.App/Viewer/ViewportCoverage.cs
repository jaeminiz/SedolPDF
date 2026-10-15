using Avalonia;

namespace PdfEditor.App.Viewer;

public static class ViewportCoverage
{
    private const double Step = 8;

    /// <summary>뷰포트 안 페이지 면적 중 그림으로 덮인 비율 (8px 격자 표본). 페이지가 없으면 1.</summary>
    public static double Compute(Rect viewport, IEnumerable<Rect> pageRects, IEnumerable<Rect> painted)
    {
        var pages = pageRects.ToArray();
        var paint = painted.ToArray();
        int total = 0, covered = 0;
        for (double y = viewport.Y + Step / 2; y < viewport.Bottom; y += Step)
            for (double x = viewport.X + Step / 2; x < viewport.Right; x += Step)
            {
                var p = new Point(x, y);
                if (!Any(pages, p)) continue;
                total++;
                if (Any(paint, p)) covered++;
            }
        return total == 0 ? 1.0 : (double)covered / total;
    }

    private static bool Any(Rect[] rects, Point p)
    {
        foreach (var r in rects) if (p.X >= r.X && p.X < r.Right && p.Y >= r.Y && p.Y < r.Bottom) return true;
        return false;
    }
}
