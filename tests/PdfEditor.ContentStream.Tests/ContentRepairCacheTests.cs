using System.Text;

namespace PdfEditor.ContentStream.Tests;

/// <summary>0.14.0 안 A: 내용 지문 캐시. 전역 캐시를 쓰므로 다른 시험과 겹치지 않게 시험마다 고유한 내용을 쓴다.</summary>
public class ContentRepairCacheTests
{
    private static List<byte[]> S(params string[] s) => s.Select(x => Encoding.Latin1.GetBytes(x)).ToList();

    [Fact]
    public void SameContent_SecondCallIsCacheHit_SameResult()
    {
        var a = S("q 0.11 0 0 rg 0 0 5 5 re f Q");
        long h0 = ContentRepairCache.Hits;
        Assert.Null(ContentRepair.RepairStreamsCached(a).Data);
        Assert.Equal(h0, ContentRepairCache.Hits);          // 처음은 적중 아님
        Assert.Null(ContentRepair.RepairStreamsCached(S("q 0.11 0 0 rg 0 0 5 5 re f Q")).Data);   // 같은 바이트의 다른 배열
        Assert.Equal(h0 + 1, ContentRepairCache.Hits);
    }

    [Fact]
    public void OneByteDifferent_IsRescanned()
    {
        ContentRepair.RepairStreamsCached(S("q 0.12 0 0 rg 0 0 5 5 re f Q"));
        long h = ContentRepairCache.Hits;
        var r = ContentRepair.RepairStreamsCached(S("q 0.12 0 0 rg 0 0 5 5 re f Q Q"));   // 짝 없는 Q 하나 더
        Assert.Equal(h, ContentRepairCache.Hits);
        Assert.NotNull(r.Data);
        Assert.Equal(1, r.RestoresRemoved);
    }

    [Fact]
    public void OnlyStreamBoundaryDifferent_IsRescanned()
    {
        var one = S("q 0.13 0 0 rg 1 1 m 2 2 l S Q");
        var two = S("q 0.13 0 0 rg 1 1 m", "2 2 l S Q");
        Assert.NotEqual(ContentRepairCache.Fingerprint(one), ContentRepairCache.Fingerprint(two));
        ContentRepair.RepairStreamsCached(one);
        long h = ContentRepairCache.Hits;
        ContentRepair.RepairStreamsCached(two);
        Assert.Equal(h, ContentRepairCache.Hits);
    }

    [Fact]
    public void DamagedContent_IsNeverCachedAsClean_AndKeepsBeingRepaired()
    {
        List<byte[]> Damaged() => S("q", "q q", "q 1 0 0 RG 2 w .75 0 0 -.75 0 841.92 cm 100 100 m 500 100 l S Q .75 0 0 -.75 0 841.92 cm", "S Q q 0 0 1 RG 2 w 100 300 m 500 300 l S Q", "Q", "Q Q");
        var r1 = ContentRepair.RepairStreamsCached(Damaged());
        long h = ContentRepairCache.Hits;
        var r2 = ContentRepair.RepairStreamsCached(Damaged());
        Assert.NotNull(r1.Data);
        Assert.NotNull(r2.Data);
        Assert.Equal(h, ContentRepairCache.Hits);
        Assert.Equal(r1.Data, r2.Data);
    }
}
