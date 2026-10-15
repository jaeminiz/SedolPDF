using PdfEditor.App.Viewer;
using PdfEditor.Document;

namespace PdfEditor.App.Tests.Viewer;

public class TileCacheTests
{
    private static TileKey K(long page, int ver, int scaleKey, int x = 0, int y = 0) => new(new PageId(page), ver, scaleKey, x, y);

    [Fact]
    public void Cache_EvictsLeastRecentlyUsed_OverBudget()
    {
        var evicted = new List<string>();
        var c = new TileCache<string>(100, s => evicted.Add(s));
        c.Add(K(1, 1, 1000), "a", 40);
        c.Add(K(1, 1, 1000, 1), "b", 40);
        Assert.True(c.TryGet(K(1, 1, 1000), out _)); // a becomes most recent
        c.Add(K(1, 1, 1000, 2), "c", 40);            // 120 > 100 -> evict b
        Assert.Equal(new[] { "b" }, evicted);
        Assert.False(c.TryGet(K(1, 1, 1000, 1), out _));
        Assert.True(c.TryGet(K(1, 1, 1000), out var a)); Assert.Equal("a", a);
        Assert.Equal(80, c.UsedBytes);
    }

    [Fact]
    public void Cache_Fallback_PicksNearestOtherScale()
    {
        var c = new TileCache<string>(1000, _ => { });
        c.Add(K(1, 1, 500), "h", 1);
        c.Add(K(1, 1, 1000, 0, 0), "o1", 1);
        c.Add(K(1, 1, 1000, 1, 0), "o2", 1);
        c.Add(K(1, 1, 4000), "q", 1);
        c.Add(K(2, 1, 1250), "otherPage", 1);
        var fb = c.FallbackFor(new PageId(1), 1, 1250);
        Assert.Equal(new[] { "o1", "o2" }, fb.Select(f => f.Image).OrderBy(s => s));
        Assert.Empty(c.FallbackFor(new PageId(1), 2, 1250)); // other version
        Assert.DoesNotContain(c.FallbackFor(new PageId(1), 1, 1000), f => f.Key.ScaleKey == 1000);
    }

    [Fact]
    public void Cache_RemoveStaleVersions_And_RemovePage()
    {
        var evicted = new List<string>();
        var c = new TileCache<string>(1000, s => evicted.Add(s));
        c.Add(K(1, 1, 1000), "v1", 10);
        c.Add(K(1, 2, 1000), "v2", 10);
        c.Add(K(2, 1, 1000), "p2", 10);
        c.RemoveStaleVersions(new PageId(1), 2);
        Assert.Equal(new[] { "v1" }, evicted);
        Assert.False(c.TryGet(K(1, 1, 1000), out _));
        Assert.True(c.TryGet(K(1, 2, 1000), out _));
        c.RemovePage(new PageId(1));
        Assert.False(c.TryGet(K(1, 2, 1000), out _));
        Assert.True(c.TryGet(K(2, 1, 1000), out _));
        Assert.Equal(10, c.UsedBytes);
    }

    [Fact]
    public void Cache_AddSameKey_ReplacesAndEvictsOld()
    {
        var evicted = new List<string>();
        var c = new TileCache<string>(1000, s => evicted.Add(s));
        c.Add(K(1, 1, 1000), "old", 10);
        c.Add(K(1, 1, 1000), "new", 20);
        Assert.Equal(new[] { "old" }, evicted);
        Assert.Equal(20, c.UsedBytes);
    }

    [Fact]
    public void Thumbnails_KeyedByPageAndVersion_Lru()
    {
        var evicted = new List<string>();
        var t = new ThumbnailCache<string>(100, s => evicted.Add(s));
        t.Add(new PageId(1), 1, "a", 60);
        t.Add(new PageId(2), 1, "b", 60);
        Assert.Equal(new[] { "a" }, evicted);
        Assert.False(t.TryGet(new PageId(1), 1, out _));
        Assert.True(t.TryGet(new PageId(2), 1, out _));
        Assert.False(t.TryGet(new PageId(2), 2, out _));
        Assert.Equal(160, ThumbnailCache<string>.LongSidePx);
        Assert.Equal(32L * 1024 * 1024, ThumbnailCache<string>.DefaultBudgetBytes);
    }
}
