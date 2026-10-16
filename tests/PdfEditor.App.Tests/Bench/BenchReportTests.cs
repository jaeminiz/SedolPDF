using System.Text.Json;
using PdfEditor.App.Bench;
using Xunit;

namespace PdfEditor.App.Tests.Bench;

public class BenchReportTests
{
    [Fact]
    public void Percentile_NearestRank()
    {
        double[] v = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        Assert.Equal(50, BenchStats.Percentile(v, 50));
        Assert.Equal(100, BenchStats.Percentile(v, 95));
        Assert.Equal(0, BenchStats.Percentile([], 50));
    }

    [Fact]
    public void GrayRuns_LongestConsecutiveGrayDuration()
    {
        // 프레임 시각(ms)과 덮임 비율. 0.99 미만이면 회색 프레임.
        double[] t = [0, 16, 32, 48, 64, 80];
        double[] cov = [1, 0.5, 0.6, 1, 0.9, 1];
        var (avgGray, maxRunMs) = BenchStats.GrayStats(t, cov);
        Assert.Equal((0 + 0.5 + 0.4 + 0 + 0.1 + 0) / 6, avgGray, 6);
        Assert.Equal(32, maxRunMs, 6);   // 16~48ms 구간 (프레임 2개가 회색, 다음 정상 프레임 시각까지)
    }

    [Fact]
    public void ToJson_HasExpectedShape()
    {
        var r = new BenchReport
        {
            Pdf = "a.pdf", PageCount = 3, ViewportWidth = 800, ViewportHeight = 600,
            OpenMs = 50, FirstPagesVisibleMs = 60, FirstScreenMaxFrameGapMs = 5, FirstTileRenderedMs = 70, FirstScreenCurrentScaleMs = 120.5, FirstScreenAnyPaintMs = 80.2,
            TileCount = 4, TileP50Ms = 10, TileP95Ms = 20, TileMaxMs = 30,
            Scroll = new ScrollResult { Scale = 1, Frames = 10, DurationMs = 500, AvgGrayRatio = 0.01, MaxGrayRunMs = 50, AvgNotCurrentScaleRatio = 0.02 },
            Fast = new ScrollResult { Scale = 1, Frames = 2 },
            Sweep = new ScrollResult { Scale = 1, Frames = 5, DurationMs = 100, AvgGrayRatio = 0.1, MaxGrayRunMs = 90, AvgNotCurrentScaleRatio = 0.2 },
            PeakWorkingSetMb = 123.4,
        };
        using var doc = JsonDocument.Parse(r.ToJson());
        var root = doc.RootElement;
        foreach (var k in new[] { "pdf", "pageCount", "viewportWidth", "viewportHeight", "openMs", "firstPagesVisibleMs", "firstScreenMaxFrameGapMs", "firstTileRenderedMs", "firstScreenCurrentScaleMs",
                     "firstScreenAnyPaintMs", "tileCount", "tileP50Ms", "tileP95Ms", "tileMaxMs", "scroll", "sweep", "fast", "peakWorkingSetMb" })
            Assert.True(root.TryGetProperty(k, out _), k);
        var s = root.GetProperty("scroll");
        foreach (var k in new[] { "scale", "frames", "durationMs", "avgGrayRatio", "maxGrayRunMs", "avgNotCurrentScaleRatio" })
            Assert.True(s.TryGetProperty(k, out _), k);
        Assert.Equal(3, root.GetProperty("pageCount").GetInt32());
    }
}
