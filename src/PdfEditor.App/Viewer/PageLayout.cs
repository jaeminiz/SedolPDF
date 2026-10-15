using Avalonia;
using PdfEditor.Document;

namespace PdfEditor.App.Viewer;

public enum ViewMode { Continuous, SinglePage }

/// <summary>문서 좌표(화면 픽셀 단위)에서 페이지를 어떻게 놓을지 계산하는 순수 클래스.</summary>
public sealed class PageLayout
{
    public const double GapPx = 12;
    public const double MarginPx = 16;

    private readonly Rect[] _rects;

    private PageLayout(Rect[] rects, Size extent, double scale, ViewMode mode)
    {
        _rects = rects;
        Extent = extent;
        Scale = scale;
        Mode = mode;
    }

    public Size Extent { get; }
    public double Scale { get; }
    public ViewMode Mode { get; }
    public int PageCount => _rects.Length;

    public static PageLayout Build(IReadOnlyList<PageInfo> pages, double scale, double viewportWidth, ViewMode mode, int singlePageIndex = 0)
    {
        var rects = new Rect[pages.Count];
        if (pages.Count == 0) return new PageLayout(rects, new Size(Math.Max(viewportWidth, 2 * MarginPx), 2 * MarginPx), scale, mode);

        if (mode == ViewMode.SinglePage)
        {
            int idx = Math.Clamp(singlePageIndex, 0, pages.Count - 1);
            double w = pages[idx].WidthPt * scale, h = pages[idx].HeightPt * scale;
            double total = Math.Max(viewportWidth, w + 2 * MarginPx);
            rects[idx] = new Rect(Math.Max(MarginPx, (total - w) / 2), MarginPx, w, h);
            return new PageLayout(rects, new Size(total, h + 2 * MarginPx), scale, mode);
        }

        double maxW = 0;
        foreach (var p in pages) maxW = Math.Max(maxW, p.WidthPt * scale);
        double totalW = Math.Max(viewportWidth, maxW + 2 * MarginPx);
        double y = MarginPx;
        for (int i = 0; i < pages.Count; i++)
        {
            double w = pages[i].WidthPt * scale, h = pages[i].HeightPt * scale;
            rects[i] = new Rect(Math.Max(MarginPx, (totalW - w) / 2), y, w, h);
            y += h + GapPx;
        }
        return new PageLayout(rects, new Size(totalW, y - GapPx + MarginPx), scale, mode);
    }

    /// <summary>SinglePage 모드에서 배치되지 않은 페이지는 크기 0인 Rect.</summary>
    public Rect GetPageRect(int index) => _rects[index];

    private static double OverlapHeight(Rect a, Rect b)
    {
        if (a.Width <= 0 || a.Height <= 0) return 0;
        double w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        double h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? h : 0;
    }

    public IReadOnlyList<int> PagesIntersecting(Rect viewport)
    {
        var res = new List<int>();
        for (int i = 0; i < _rects.Length; i++)
            if (OverlapHeight(_rects[i], viewport) > 0) res.Add(i);
        return res;
    }

    public int CurrentPage(Rect viewport)
    {
        int best = -1; double bestH = 0;
        for (int i = 0; i < _rects.Length; i++)
        {
            double h = OverlapHeight(_rects[i], viewport);
            if (h > bestH + 1e-9) { best = i; bestH = h; }
        }
        if (best >= 0) return best;
        // 보이는 페이지가 없으면 뷰포트 중심에 가장 가까운 페이지
        var c = viewport.Center;
        double bd = double.MaxValue;
        for (int i = 0; i < _rects.Length; i++)
        {
            if (_rects[i].Width <= 0) continue;
            double d = DistanceTo(_rects[i], c);
            if (d < bd) { bd = d; best = i; }
        }
        return Math.Max(best, 0);
    }

    public (int Page, Point InPage)? HitTest(Point doc)
    {
        for (int i = 0; i < _rects.Length; i++)
        {
            var r = _rects[i];
            if (r.Width > 0 && doc.X >= r.X && doc.X < r.Right && doc.Y >= r.Y && doc.Y < r.Bottom)
                return (i, new Point(doc.X - r.X, doc.Y - r.Y));
        }
        return null;
    }

    internal int NearestPage(Point doc)
    {
        int best = -1; double bd = double.MaxValue;
        for (int i = 0; i < _rects.Length; i++)
        {
            if (_rects[i].Width <= 0) continue;
            double d = DistanceTo(_rects[i], doc);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    private static double DistanceTo(Rect r, Point p)
    {
        double dx = Math.Max(Math.Max(r.X - p.X, 0), p.X - r.Right);
        double dy = Math.Max(Math.Max(r.Y - p.Y, 0), p.Y - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
