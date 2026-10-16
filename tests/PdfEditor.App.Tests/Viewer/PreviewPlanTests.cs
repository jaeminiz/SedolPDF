using PixelRect = PdfEditor.App.Viewer.PixelRect;
using Avalonia;
using PdfEditor.App.Viewer;
using PdfEditor.Document;

namespace PdfEditor.App.Tests.Viewer;

public class PreviewPlanTests
{
    [Fact]
    public void ChooseScale_TypicalViewport_HalfResolution()
    {
        // 782x581 px 뷰포트 = 454k px > 300k → 절반 이하이므로 0.5로 고정(최대 비율)
        int sk = PreviewPlan.ChooseScaleKey(1.333, 782 / 1.333, 581 / 1.333);
        Assert.InRange(sk / 1000.0 / 1.333, 0.45, 0.5);
    }

    [Fact]
    public void ChooseScale_HugeViewport_ReducedFurtherButFloored()
    {
        double full = 1.333;
        int sk = PreviewPlan.ChooseScaleKey(full, 3840 / full, 2160 / full);   // 8.3M px
        double factor = sk / 1000.0 / full;
        Assert.True(factor < 0.25);
        Assert.InRange(factor, PreviewPlan.MinFactor - 0.01, PreviewPlan.MaxFactor);
        // 상한을 넘지 않는 픽셀 수(바닥에 걸리지 않는 경우)
        int sk2 = PreviewPlan.ChooseScaleKey(full, 1600 / full, 1200 / full);
        double px = 1600.0 * 1200 * Math.Pow(sk2 / 1000.0 / full, 2);
        Assert.True(px <= PreviewPlan.MaxPixels * 1.02);
    }

    [Fact]
    public void ChooseScale_SmallRegion_NeverUpscalesAboveHalf()
    {
        int sk = PreviewPlan.ChooseScaleKey(1.0, 100, 100);
        Assert.Equal(500, sk);
    }

    [Fact]
    public void RegionPixels_ClampsToPage_AndRoundsOutward()
    {
        var p = VT.P(1, 1684, 2384);
        var r = PreviewPlan.RegionPixels(p, 500, new Rect(-10, 100.3, 600.2, 5000))!.Value;
        Assert.Equal(0, r.X);
        Assert.Equal(50, r.Y);                       // floor(100.3*.5)
        Assert.Equal(296, r.W);                      // ceil(590.2*.5)=296 - 0
        Assert.Equal(1192 - 50, r.H);                // 페이지 끝(2384*.5)에서 잘림
        Assert.Null(PreviewPlan.RegionPixels(p, 500, new Rect(5000, 0, 10, 10)));
    }

    [Fact]
    public void Key_IsPreview_ScaleIsPositive_DestRectMatchesRegion()
    {
        var p = VT.P(1, 1684, 2384);
        var region = new PixelRect(100, 200, 300, 400);
        var req = PreviewPlan.RequestFor(p, 500, region);
        Assert.Equal(TileKind.Preview, req.Kind);
        Assert.True(req.Key.IsPreview);
        Assert.Equal(0.5, req.Scale, 6);
        Assert.Equal(0.5, req.Key.Scale, 6);
        // 지금 배율 1.0 DIP/pt, 쪽이 (50,60)에 있을 때: 픽셀 100 → 200pt
        var dest = PreviewPlan.DestRect(req.Key, 300, 400, new Rect(50, 60, 1684, 2384), 1.0);
        Assert.Equal(new Rect(250, 460, 600, 800), dest);
    }

    [Fact]
    public void Covers_AllowsOnePixelTolerance()
    {
        var key = PreviewPlan.KeyFor(VT.P(1), 500, new PixelRect(100, 100, 400, 300));
        var cov = PreviewPlan.CoveredPt(key, 400, 300);   // (200,200,800,600) pt
        Assert.True(PreviewPlan.Covers(cov, 0.5, new Rect(200, 200, 800, 600)));
        Assert.True(PreviewPlan.Covers(cov, 0.5, new Rect(250, 250, 500, 400)));
        Assert.False(PreviewPlan.Covers(cov, 0.5, new Rect(150, 200, 800, 600)));
        Assert.False(PreviewPlan.Covers(cov, 0.5, new Rect(200, 200, 900, 600)));
    }

    [Fact]
    public void Planner_LargePage_NoCoverage_PreviewFirst_ThenTiles()
    {
        var pages = new[] { VT.P(1, 1684, 2384) };
        double dip = ZoomMath.DipPerPoint(1.0);
        var layout = PageLayout.Build(pages, dip, 782, ViewMode.Continuous);
        var vp = new Rect(0, 0, 782, 581);
        var plan = PrefetchPlanner.Plan(pages, layout, vp, dip, _ => false, (_, _, _) => false, 1.0, (_, _) => false);
        Assert.Equal(TileKind.Preview, plan[0].Kind);
        Assert.DoesNotContain(plan.Skip(1), r => r.Kind == TileKind.Preview);
        Assert.Equal(TileKind.Tile, plan[1].Kind);
        // 미리보기는 보이는 영역만(뷰포트 크기 정도)이고 풀 배율의 절반 이하
        Assert.True(plan[0].Scale <= dip * PreviewPlan.MaxFactor + 1e-3);
        Assert.True((long)plan[0].Rect.W * plan[0].Rect.H <= PreviewPlan.MaxPixels * 1.1);
    }

    [Fact]
    public void Planner_PreviewAlreadyCovered_OrFewTiles_OrDisabled_NoPreview()
    {
        var pages = new[] { VT.P(1, 1684, 2384), VT.P(2, 595, 842) };
        double dip = 1.0;
        var layout = PageLayout.Build(pages, dip, 782, ViewMode.Continuous);
        var vp = new Rect(0, 0, 782, 581);
        Assert.DoesNotContain(PrefetchPlanner.Plan(pages, layout, vp, dip, _ => false, (_, _, _) => false, 1.0, (_, _) => true), r => r.Kind == TileKind.Preview);
        Assert.DoesNotContain(PrefetchPlanner.Plan(pages, layout, vp, dip, _ => false, (_, _, _) => false), r => r.Kind == TileKind.Preview);
        // 작은 쪽(타일 1장)만 보이면 미리보기 없음
        var small = PageLayout.Build(new[] { pages[1] }, dip, 782, ViewMode.Continuous);
        Assert.DoesNotContain(PrefetchPlanner.Plan(new[] { pages[1] }, small, vp, dip, _ => false, (_, _, _) => false, 1.0, (_, _) => false), r => r.Kind == TileKind.Preview);
        // 보이는 타일이 거의 다 캐시에 있으면(없는 것 2장) 미리보기 없음
        int n = 0;
        var plan = PrefetchPlanner.Plan(pages, layout, vp, dip, _ => n++ >= 2, (_, _, _) => false, 1.0, (_, _) => false);
        Assert.DoesNotContain(plan, r => r.Kind == TileKind.Preview);
    }

    [Fact]
    public void Planner_ScrolledPage_RegionIsRelativeToPage()
    {
        var pages = new[] { VT.P(1, 1684, 2384) };
        var layout = PageLayout.Build(pages, 1.0, 782, ViewMode.Continuous);
        Rect? seen = null;
        PrefetchPlanner.Plan(pages, layout, new Rect(0, 1000, 782, 581), 1.0, _ => false, (_, _, _) => false, 1.0, (_, r) => { seen = r; return true; });
        var pr = layout.GetPageRect(0);
        Assert.NotNull(seen);
        Assert.Equal(1000 - pr.Y, seen!.Value.Y, 3);
        Assert.Equal(581, seen.Value.Height, 3);
    }

    [Fact]
    public void Cache_PreviewsFor_AreIsolatedFromTilesAndFallback()
    {
        var c = new TileCache<string>(1000, _ => { });
        var page = new PageId(1);
        var pk = PreviewPlan.KeyFor(VT.P(1), 500, new PixelRect(0, 0, 10, 10));
        c.Add(pk, "preview", 10);
        c.Add(new TileKey(page, 1, 0, 1333, 0, 0), "tile", 10);
        var prev = c.PreviewsFor(page, 1);
        Assert.Equal("preview", Assert.Single(prev).Image);
        Assert.DoesNotContain(c.FallbackFor(page, 1, 0, 2000), f => f.Key.IsPreview);
        Assert.Empty(c.PreviewsFor(page, 2));
        c.RemoveStaleVersions(page, 2);
        Assert.Empty(c.PreviewsFor(page, 1));
        Assert.Equal(0, c.UsedBytes);
    }

    [Fact]
    public void Cache_PreviewsCountAgainstSameBudget()
    {
        var c = new TileCache<string>(100, _ => { });
        c.Add(new TileKey(new PageId(1), 1, 0, 1000, 0, 0), "t", 60);
        c.Add(PreviewPlan.KeyFor(VT.P(1), 500, new PixelRect(0, 0, 1, 1)), "p", 60);
        Assert.True(c.UsedBytes <= 100);
    }
}
