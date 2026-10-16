using Avalonia;
using PdfEditor.Document;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 지금 필요한 렌더 요청을 우선순위 순서로 계산하는 순수 클래스.
/// 보이는 타일(뷰포트 중심에서 문서 좌표 거리순) → 화면 밖 한 타일 링 → 보이는 페이지 ±3쪽 썸네일.
/// 이미 캐시에 있는 것은 뺀다.
/// </summary>
public static class PrefetchPlanner
{
    public const int ThumbnailPageRadius = 3;

    public static List<TileRequest> Plan(IReadOnlyList<PageInfo> pages, PageLayout layout, Rect viewport, double scale,
        Func<TileKey, bool> hasTile, Func<PageId, int, bool> hasThumbnail, double renderScaling = 1.0)
    {
        var visibleIdx = layout.PagesIntersecting(viewport);
        var visible = new List<TileRequest>();
        var visibleKeys = new HashSet<TileKey>();
        var rects = new Dictionary<PageId, Rect>();

        foreach (int i in visibleIdx)
        {
            var page = pages[i];
            var rect = layout.GetPageRect(i);
            rects[page.Id] = rect;
            foreach (var key in TileGrid.Tiles(page, scale, rect, viewport, 0, renderScaling))
            {
                visibleKeys.Add(key);
                if (!hasTile(key)) visible.Add(DocumentRenderer.TileRequestFor(page, key));
            }
        }

        // 링: 뷰포트를 한 타일 크기만큼 넓혀 걸치는 페이지의 타일 (이미 보이는 것 제외)
        var ring = new List<TileRequest>();
        var wide = viewport.Inflate(TileGrid.TileSize / renderScaling);
        foreach (int i in layout.PagesIntersecting(wide))
        {
            var page = pages[i];
            var rect = layout.GetPageRect(i);
            rects[page.Id] = rect;
            foreach (var key in TileGrid.Tiles(page, scale, rect, viewport, 1, renderScaling))
                if (!visibleKeys.Contains(key) && !hasTile(key)) ring.Add(DocumentRenderer.TileRequestFor(page, key));
        }

        // 썸네일: 보이는 페이지 ±3쪽, 보이는 쪽 중심에서 가까운 순
        var thumbs = new List<TileRequest>();
        if (visibleIdx.Count > 0)
        {
            int lo = Math.Max(0, visibleIdx.Min() - ThumbnailPageRadius);
            int hi = Math.Min(pages.Count - 1, visibleIdx.Max() + ThumbnailPageRadius);
            int center = layout.CurrentPage(viewport);
            for (int i = lo; i <= hi; i++)
                if (!hasThumbnail(pages[i].Id, pages[i].Version)) thumbs.Add(DocumentRenderer.ThumbnailRequestFor(pages[i]));
            var idxOf = pages.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
            thumbs = thumbs.OrderBy(t => Math.Abs(idxOf[t.Key.Page] - center)).ToList();
        }

        // 요청의 문서 좌표 중심: 페이지 위치 + (페이지 안 픽셀 / 그 요청의 배율 × 현재 배율)
        Point DocCenter(TileRequest r)
        {
            if (!rects.TryGetValue(r.Key.Page, out var pr)) return default;
            double k = r.Kind == TileKind.Thumbnail ? 0 : scale / r.Scale;
            if (r.Kind == TileKind.Thumbnail) return pr.Center;
            return new Point(pr.X + (r.Rect.X + r.Rect.W / 2.0) * k, pr.Y + (r.Rect.Y + r.Rect.H / 2.0) * k);
        }

        return TilePriority.Order(visible, ring, thumbs, viewport.Center, DocCenter).ToList();
    }
}
