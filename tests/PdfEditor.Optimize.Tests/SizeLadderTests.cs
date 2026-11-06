namespace PdfEditor.Optimize.Tests;

public class SizeLadderTests
{
    private static (Func<int, long> Measure, List<int> Calls) Fake(params long[] sizes)
    {
        var calls = new List<int>();
        return (i => { calls.Add(i); return sizes[i]; }, calls);
    }

    [Fact]
    public void Ladders_HaveTheSpecifiedLevels_AndNeverGetMilder()
    {
        Assert.Equal(5, SizeLadder.Image.Count);
        Assert.Equal(new LadderLevel(300, 85), SizeLadder.Image[0]);
        Assert.Equal(new LadderLevel(96, 45), SizeLadder.Image[^1]);
        Assert.Equal(3, SizeLadder.Raster.Count);
        Assert.Equal(new LadderLevel(200, 75), SizeLadder.Raster[0]);
        Assert.Equal(new LadderLevel(120, 55), SizeLadder.Raster[^1]);
        foreach (var ladder in new[] { SizeLadder.Image, SizeLadder.Raster })
            for (int i = 1; i < ladder.Count; i++)
            {
                Assert.True(ladder[i].MaxDpi <= ladder[i - 1].MaxDpi);
                Assert.True(ladder[i].JpegQuality <= ladder[i - 1].JpegQuality);
            }
    }

    [Fact]
    public void FirstFitting_AllLevelsFit_PicksTheMildestLevel()
    {
        var (m, _) = Fake(50, 45, 40, 35, 30);
        var r = SizeLadder.FirstFitting(5, m, 100);
        Assert.Equal(0, r.Level);
        Assert.Equal(50, r.BytesAtLevel);
    }

    [Fact]
    public void FirstFitting_FitsFromTheThirdLevel_PicksIt_WithFewMeasurements()
    {
        var (m, calls) = Fake(100, 90, 80, 70, 60);
        var r = SizeLadder.FirstFitting(5, m, 85);
        Assert.Equal(2, r.Level);
        Assert.Equal(80, r.BytesAtLevel);
        Assert.True(calls.Count < 5, "binary search should not measure every level: " + string.Join(",", calls));
    }

    [Fact]
    public void FirstFitting_NothingFits_ReportsStrongestSize()
    {
        var (m, _) = Fake(100, 90, 80, 70, 60);
        var r = SizeLadder.FirstFitting(5, m, 50);
        Assert.Null(r.Level);
        Assert.Equal(60, r.BytesAtStrongest);
    }

    [Fact]
    public void FirstFitting_SizeEqualToTarget_Fits()
    {
        var (m, _) = Fake(100, 90, 80, 70, 60);
        Assert.Equal(2, SizeLadder.FirstFitting(5, m, 80).Level);
    }

    [Fact]
    public void FirstFitting_NoLevels_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => SizeLadder.FirstFitting(0, _ => 0, 10));
}
