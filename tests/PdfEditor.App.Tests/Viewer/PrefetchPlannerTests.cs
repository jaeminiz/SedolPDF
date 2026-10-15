using Avalonia;
using PdfEditor.App.Viewer;
using PdfEditor.Document;

namespace PdfEditor.App.Tests.Viewer;

public class PrefetchPlannerTests
{
    private static PageInfo[] Pages(int n, float w = 600, float h = 800) =>
        Enumerable.Range(1, n).Select(i => new PageInfo(new PageId(i), 0, w, h)).ToArray();

    [Fact]
    public void Plan_VisibleFirstByDocDistance_ThenRing_ThenThumbnails()
    {
        var pages = Pages(3);
        var layout = PageLayout.Build(pages, 1.0, 632, ViewMode.Continuous);
        var vp = new Rect(0, 1000, 632, 400);   // 페이지 2(인덱스 1)만 보임, 0과 2는 링
        var plan = PrefetchPlanner.Plan(pages, layout, vp, 1.0, _ => false, (_, _) => false);

        Assert.Equal(TileKind.Tile, plan[0].Kind);
        Assert.Equal(pages[1].Id, plan[0].Key.Page);
        var ringPages = plan.Skip(1).Where(r => r.Kind == TileKind.Tile).Select(r => r.Key.Page).ToHashSet();
        Assert.Equal(new HashSet<PageId> { pages[0].Id, pages[2].Id }, ringPages);
        var firstThumb = plan.FindIndex(r => r.Kind == TileKind.Thumbnail);
        Assert.All(plan.Skip(firstThumb), r => Assert.Equal(TileKind.Thumbnail, r.Kind));
        Assert.Equal(pages[1].Id, plan[firstThumb].Key.Page);   // 현재 쪽에서 가까운 순
    }

    [Fact]
    public void Plan_MultiPage_UsesDocumentSpaceDistance()
    {
        // 두 페이지가 같이 보일 때, 뷰포트 중심에 더 가까운 페이지의 타일이 먼저여야 한다.
        var pages = Pages(2);
        var layout = PageLayout.Build(pages, 1.0, 632, ViewMode.Continuous);
        var vp = new Rect(0, 700, 632, 400);   // 중심 y=900. 페이지0 중심 416, 페이지1 중심 1228
        var plan = PrefetchPlanner.Plan(pages, layout, vp, 1.0, _ => false, (_, _) => false);
        Assert.Equal(pages[1].Id, plan[0].Key.Page);
        Assert.Equal(pages[0].Id, plan[1].Key.Page);
    }

    [Fact]
    public void Plan_SkipsAlreadyCached()
    {
        var pages = Pages(2);
        var layout = PageLayout.Build(pages, 1.0, 632, ViewMode.Continuous);
        var vp = new Rect(0, 0, 632, 400);
        var plan = PrefetchPlanner.Plan(pages, layout, vp, 1.0, _ => true, (_, _) => true);
        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_Thumbnails_LimitedToThreePagesAroundVisible()
    {
        var pages = Pages(20);
        var layout = PageLayout.Build(pages, 1.0, 632, ViewMode.Continuous);
        double y = layout.GetPageRect(10).Y;
        var plan = PrefetchPlanner.Plan(pages, layout, new Rect(0, y, 632, 400), 1.0, _ => false, (_, _) => false);
        var thumbs = plan.Where(r => r.Kind == TileKind.Thumbnail).Select(r => (int)(r.Key.Page.Value - 1)).ToList();
        Assert.Equal(Enumerable.Range(7, 7), thumbs.Order());
    }
}
