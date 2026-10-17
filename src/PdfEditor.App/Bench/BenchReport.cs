using System.Text.Json;

namespace PdfEditor.App.Bench;

public static class BenchStats
{
    /// <summary>가장 가까운 순위(nearest-rank) 백분위수. 비어 있으면 0.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var s = values.OrderBy(x => x).ToArray();
        int rank = (int)Math.Ceiling(p / 100.0 * s.Length);
        return s[Math.Clamp(rank, 1, s.Length) - 1];
    }

    /// <summary>
    /// 프레임별 덮임 비율(0~1)에서 평균 회색 비율과 "회색이 이어진 최대 시간(ms)"을 구한다.
    /// 회색 프레임 = 덮임 &lt; 0.99. 이어진 시간 = 첫 회색 프레임 시각부터 그 다음 정상 프레임 시각까지(끝까지 회색이면 마지막 프레임 시각까지).
    /// </summary>
    public static (double AvgGray, double MaxRunMs) GrayStats(IReadOnlyList<double> timesMs, IReadOnlyList<double> coverage)
    {
        int n = coverage.Count;
        if (n == 0) return (0, 0);
        double sum = 0, maxRun = 0, runStart = double.NaN;
        for (int i = 0; i < n; i++)
        {
            sum += 1 - coverage[i];
            bool gray = coverage[i] < 0.99;
            if (gray && double.IsNaN(runStart)) runStart = timesMs[i];
            if (!gray && !double.IsNaN(runStart)) { maxRun = Math.Max(maxRun, timesMs[i] - runStart); runStart = double.NaN; }
        }
        if (!double.IsNaN(runStart)) maxRun = Math.Max(maxRun, timesMs[n - 1] - runStart);
        return (sum / n, maxRun);
    }
}

public sealed class ScrollResult
{
    public double Scale { get; init; }
    public int Frames { get; init; }
    public double DurationMs { get; init; }
    /// <summary>평균 (1 - LastCoverage): 아무 그림으로도 안 덮인 비율.</summary>
    public double AvgGrayRatio { get; init; }
    public double MaxGrayRunMs { get; init; }
    /// <summary>평균 (1 - 현재 배율 타일 덮임): 흐릿한 대체 그림이거나 회색인 비율.</summary>
    public double AvgNotCurrentScaleRatio { get; init; }
}

public sealed class BenchReport
{
    public string Pdf { get; init; } = "";
    public int PageCount { get; init; }
    public double ViewportWidth { get; init; }
    public double ViewportHeight { get; init; }
    /// <summary>열기 시작 → OpenAsync 반환(PDFium 로드 포함)까지.</summary>
    public double OpenMs { get; init; }
    /// <summary>열기 시작 → 문서 화면이 처음 쪽을 그린 시각(타일은 아직 없을 수 있음).</summary>
    public double FirstPagesVisibleMs { get; init; }
    /// <summary>열기 시작 → 첫 타일 렌더가 PDFium 스레드에서 끝난 시각.</summary>
    public double FirstTileRenderedMs { get; init; }
    /// <summary>첫 화면을 기다리는 동안 가장 긴 프레임 간격(UI 스레드가 막힌 시간의 근사).</summary>
    public double FirstScreenMaxFrameGapMs { get; init; }
    /// <summary>첫 화면 대기 중 프레임별 "시각ms 쪽수 아무그림 현재배율" (진단용, 최대 120개).</summary>
    public List<string> FirstScreenTrace { get; init; } = new();
    public double FirstScreenCurrentScaleMs { get; init; }
    public double FirstScreenAnyPaintMs { get; init; }
    public int TileCount { get; init; }
    public double TileP50Ms { get; init; }
    public double TileP95Ms { get; init; }
    public double TileMaxMs { get; init; }
    public ScrollResult? Scroll { get; init; }
    public ScrollResult? Sweep { get; init; }
    /// <summary>3초 안에 끝까지 가는 속도(프레임 60개/초 기준 한 프레임에 전체 길이의 1/180)로 스크롤한 결과.</summary>
    public ScrollResult? Fast { get; init; }
    public double PeakWorkingSetMb { get; init; }
    /// <summary>`--annots N`로 올린 주석 개수(0 = 재지 않음).</summary>
    public int OverlayAnnotCount { get; init; }
    /// <summary>올린 주석 종류(`--annots` = Ink, `--textboxes` = FreeText).</summary>
    public string OverlayAnnotKind { get; init; } = "";
    /// <summary>프레임마다 주석 층(AnnotationOverlay.DrawPage)을 그리는 시간의 95번째 백분위수(ms). 주석을 올리지 않았으면 0.</summary>
    public double OverlayDrawMsP95 { get; init; }
    public double OverlayDrawMsP50 { get; init; }
    public double OverlayDrawMsMax { get; init; }
    public int OverlayDrawSamples { get; init; }

    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string ToJson() => JsonSerializer.Serialize(this, Opt);
}
