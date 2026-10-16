using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfEditor.App.Documents;
using PdfEditor.App.Shell;
using PdfEditor.App.Viewer;

namespace PdfEditor.App.Bench;

public sealed record BenchOptions(string Pdf, string Out);

/// <summary>
/// `--bench &lt;pdf&gt; --out &lt;json&gt;`: 창을 띄워 문서를 열고 첫 화면 시간·스크롤 중 회색 비율·타일 렌더 시간·최대 작업 집합을 JSON으로 쓰고 종료한다.
/// 입력 장치는 쓰지 않는다. 프레임 콜백(RequestAnimationFrame)에서 스크롤 위치를 직접 바꾼다.
/// </summary>
public static class BenchRunner
{
    private const double Full = 0.999;   // 8px 격자 표본에서 "다 덮임"으로 보는 기준

    public static BenchOptions? Parse(string[] args)
    {
        string? pdf = null, outPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bench" && i + 1 < args.Length) pdf = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
        }
        return pdf is null || outPath is null ? null : new BenchOptions(Path.GetFullPath(pdf), Path.GetFullPath(outPath));
    }

    public static async Task RunAsync(MainWindow window, BenchOptions o)
    {
        try
        {
            var report = await MeasureAsync(window, o);
            File.WriteAllText(o.Out, report.ToJson());
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(o.Out, "{ \"error\": " + System.Text.Json.JsonSerializer.Serialize(ex.ToString()) + " }");
            Environment.Exit(1);
        }
    }

    /// <summary>다음 프레임(문서 화면의 Render 호출)까지 기다린다. 화면이 아직 없으면 짧게 양보한다. 200 ms가 지나도 안 그려지면 그냥 돌아온다.</summary>
    private static async Task Frame(DocumentView? view)
    {
        long c = view?.RenderCount ?? 0;
        view?.InvalidateVisual();
        var sw = Stopwatch.StartNew();
        do
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            if (view is null || view.RenderCount != c) return;
        } while (sw.ElapsedMilliseconds < 200);
    }

    private static async Task<BenchReport> MeasureAsync(MainWindow window, BenchOptions o)
    {
        var tiles = new List<double>();
        long openStart = 0; double firstTile = double.NaN;
        DocumentRenderer.TileRenderTimed = ms =>
        {
            lock (tiles) { tiles.Add(ms); if (double.IsNaN(firstTile) && openStart != 0) firstTile = Stopwatch.GetElapsedTime(openStart).TotalMilliseconds; }
        };

        await Task.Delay(500);   // 창이 뜨고 초기 레이아웃이 끝나도록
        var sw = Stopwatch.StartNew();
        Volatile.Write(ref openStart, Stopwatch.GetTimestamp());
        await window.ViewModel.OpenAsync(o.Pdf);
        double openMs = sw.Elapsed.TotalMilliseconds;
        var tab = window.ViewModel.ActiveDocument ?? throw new InvalidOperationException("document did not open");

        DocumentView? view = null;
        double anyMs = double.NaN, curMs = double.NaN, visMs = double.NaN;
        double lastFrame = sw.Elapsed.TotalMilliseconds, maxGap = 0;
        var trace = new List<string>();
        while (double.IsNaN(curMs))
        {
            await Frame(view);
            { double now = sw.Elapsed.TotalMilliseconds; maxGap = Math.Max(maxGap, now - lastFrame); lastFrame = now; }
            if (sw.Elapsed.TotalSeconds > 60) throw new TimeoutException("first screen not covered within 60 s");
            view ??= window.GetVisualDescendants().OfType<DocumentView>().FirstOrDefault();
            if (view is not null && trace.Count < 120) trace.Add($"{sw.Elapsed.TotalMilliseconds:F0} {view.LastVisiblePageCount} {view.LastCoverage:F4} {view.LastExactCoverage:F4}");
            if (view is null || view.LastVisiblePageCount == 0) continue;
            if (double.IsNaN(visMs)) visMs = sw.Elapsed.TotalMilliseconds;
            if (double.IsNaN(anyMs) && view.LastCoverage >= Full) anyMs = sw.Elapsed.TotalMilliseconds;
            if (view.LastExactCoverage >= Full) curMs = sw.Elapsed.TotalMilliseconds;
        }
        if (double.IsNaN(anyMs)) anyMs = curMs;

        var sv = view!.Scroller ?? throw new InvalidOperationException("scroll viewer not found");
        double vw = sv.Viewport.Width, vh = sv.Viewport.Height;

        tab.ActualSize();   // 100% (1px/pt)
        await SettleAsync(window, view);
        var scroll = await ScrollPassAsync(window, view, 1.0, vh / 4);
        var sweep = await ScrollPassAsync(window, view, 1.0, vh);
        double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        var fast = await ScrollPassAsync(window, view, 1.0, Math.Max(1, maxY / 180));

        double[] t;
        lock (tiles) t = tiles.ToArray();
        return new BenchReport
        {
            Pdf = Path.GetFileName(o.Pdf),
            PageCount = tab.PageCount,
            ViewportWidth = vw,
            ViewportHeight = vh,
            OpenMs = openMs,
            FirstPagesVisibleMs = visMs,
            FirstScreenTrace = trace,
            FirstScreenMaxFrameGapMs = maxGap,
            FirstTileRenderedMs = firstTile,
            FirstScreenCurrentScaleMs = curMs,
            FirstScreenAnyPaintMs = anyMs,
            TileCount = t.Length,
            TileP50Ms = BenchStats.Percentile(t, 50),
            TileP95Ms = BenchStats.Percentile(t, 95),
            TileMaxMs = t.Length == 0 ? 0 : t.Max(),
            Scroll = scroll,
            Sweep = sweep,
            Fast = fast,
            PeakWorkingSetMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0,
        };
    }

    private static async Task SettleAsync(TopLevel top, DocumentView view)
    {
        var sw = Stopwatch.StartNew();
        int ok = 0;
        while (ok < 3)
        {
            await Frame(view);
            if (sw.Elapsed.TotalSeconds > 60) throw new TimeoutException("screen not covered within 60 s after zoom");
            ok = view.LastVisiblePageCount > 0 && view.LastExactCoverage >= Full ? ok + 1 : 0;
        }
    }

    private static async Task<ScrollResult> ScrollPassAsync(TopLevel top, DocumentView view, double scale, double step)
    {
        var sv = view.Scroller!;
        sv.Offset = new Vector(sv.Offset.X, 0);
        await SettleAsync(top, view);
        var times = new List<double>();
        var cov = new List<double>();
        var exact = new List<double>();
        double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        double y = 0;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            await Frame(view);
            times.Add(sw.Elapsed.TotalMilliseconds);
            cov.Add(view.LastCoverage);
            exact.Add(view.LastExactCoverage);
            if (y >= maxY) break;
            y = Math.Min(y + step, maxY);
            sv.Offset = new Vector(sv.Offset.X, y);
        }
        var (avgGray, maxRun) = BenchStats.GrayStats(times, cov);
        return new ScrollResult
        {
            Scale = scale,
            Frames = times.Count,
            DurationMs = times[^1],
            AvgGrayRatio = avgGray,
            MaxGrayRunMs = maxRun,
            AvgNotCurrentScaleRatio = exact.Average(x => 1 - x),
        };
    }
}
