using System.Diagnostics;
using System.Text;
using Pdf.Native;
using S4.RenderBench;

// 사용법: dotnet run -c Release --project spikes/S4.RenderBench -- <출력폴더>
string outDir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine("spikes", "out");
Directory.CreateDirectory(outDir);
string drawing = Path.Combine(outDir, "bench-a1-drawing.pdf");
string many = Path.Combine(outDir, "bench-a4-300pages.pdf");

if (!File.Exists(drawing)) { Console.WriteLine("A1 도면 샘플 생성 중..."); BenchPdfFactory.CreateDrawing(drawing); }
if (!File.Exists(many)) { Console.WriteLine("300쪽 샘플 생성 중..."); BenchPdfFactory.CreateManyPages(many); }
Console.WriteLine($"샘플: {new FileInfo(drawing).Length / 1024} KB, {new FileInfo(many).Length / 1024} KB");

if (args.Contains("--ui")) { UiViewer.Run(drawing); return; }

var report = new StringBuilder();
void Line(string s) { Console.WriteLine(s); report.AppendLine(s); }

using var t = new PdfiumThread();
const int Tile = 512;

(double p50, double p95, double max) Stats(List<double> ms)
{
    var s = ms.OrderBy(x => x).ToList();
    return (s[(int)(s.Count * 0.50)], s[Math.Min(s.Count - 1, (int)(s.Count * 0.95))], s[^1]);
}

// ---- A1 도면 ----
double openMs = 0, loadMs = 0;
SizeInfo? size = null;
{
    var sw = Stopwatch.StartNew();
    var doc = await t.InvokeAsync(() => PdfDocument.Open(t, drawing));
    openMs = sw.Elapsed.TotalMilliseconds;
    sw.Restart();
    var page = await t.InvokeAsync(() => doc.LoadPage(0));
    loadMs = sw.Elapsed.TotalMilliseconds;
    size = await t.InvokeAsync(() => new SizeInfo(page.SizePt.Width, page.SizePt.Height));

    Line("## S4 렌더링 벤치마크 결과");
    Line($"- 측정 환경: {Environment.OSVersion}, {Environment.ProcessorCount} 논리 코어, .NET {Environment.Version}, Release={!IsDebug()}");
    Line("");
    Line("### (a) A1 도면 1쪽 (선 20만 + 텍스트 5천)");
    Line($"- 파일 열기: {openMs:F1} ms, 페이지 로드: {loadMs:F1} ms");
    Line("");
    Line("| 배율 | 타일 수(표본) | p50 ms | p95 ms | 최대 ms |");
    Line("|---|---|---|---|---|");
    foreach (var scale in new[] { 0.25, 1.0, 4.0 })
    {
        int cols = (int)Math.Ceiling(size.W * scale / Tile), rows = (int)Math.Ceiling(size.H * scale / Tile);
        var coords = new List<(int, int)>();
        int want = 30;
        for (int i = 0; i < want; i++)
            coords.Add((i * 7 % cols, i * 11 % rows));
        coords = coords.Distinct().ToList();
        // 워밍업
        await t.InvokeAsync(() => page.RenderTile(scale, 0, 0, Tile, Tile, false));
        var times = new List<double>();
        foreach (var (cx, cy) in coords)
        {
            var sw2 = Stopwatch.StartNew();
            await t.InvokeAsync(() => page.RenderTile(scale, cx * Tile, cy * Tile, Tile, Tile, false));
            times.Add(sw2.Elapsed.TotalMilliseconds);
        }
        var (p50, p95, max) = Stats(times);
        Line($"| {scale} | {times.Count} | {p50:F1} | {p95:F1} | {max:F1} |");
    }
    await t.InvokeAsync(() => { page.Dispose(); doc.Dispose(); });
}

// ---- 300쪽 문서 ----
{
    var sw = Stopwatch.StartNew();
    var doc = await t.InvokeAsync(() => PdfDocument.Open(t, many));
    double open2 = sw.Elapsed.TotalMilliseconds;
    int count = await t.InvokeAsync(() => doc.PageCount);

    Line("");
    Line("### (b) A4 300쪽 (쪽마다 텍스트 2천 자 + 이미지 1개)");
    Line($"- 파일 열기: {open2:F1} ms, 쪽 수: {count}");

    // 처음부터 끝까지: 쪽마다 화면 1장 분량(배율 1.333 = 100%, 793x1123 px → 512 타일 2x3개)
    double scale = 96.0 / 72.0;
    int tilesPerPage = 0;
    var perPage = new List<double>();
    var total = Stopwatch.StartNew();
    for (int p = 0; p < count; p++)
    {
        var sp = Stopwatch.StartNew();
        int tiles = await t.InvokeAsync(() =>
        {
            using var page = doc.LoadPage(p);
            var s = page.SizePt;
            int cols = (int)Math.Ceiling(s.Width * scale / Tile), rows = (int)Math.Ceiling(s.Height * scale / Tile);
            for (int ty = 0; ty < rows; ty++)
                for (int tx = 0; tx < cols; tx++)
                    page.RenderTile(scale, tx * Tile, ty * Tile, Tile, Tile, true);
            return cols * rows;
        });
        tilesPerPage = tiles;
        perPage.Add(sp.Elapsed.TotalMilliseconds);
    }
    total.Stop();
    var (p50b, p95b, maxb) = Stats(perPage);
    Line($"- 처음부터 끝까지 (쪽당 {tilesPerPage}타일, 100% 배율): 총 {total.Elapsed.TotalSeconds:F2} 초, 처리량 {count / total.Elapsed.TotalSeconds:F1} 쪽/초, {count * tilesPerPage / total.Elapsed.TotalSeconds:F0} 타일/초");
    Line($"- 쪽당 (로드+렌더) p50 {p50b:F1} ms, p95 {p95b:F1} ms, 최대 {maxb:F1} ms");
    await t.InvokeAsync(doc.Dispose);
}

using (var proc = Process.GetCurrentProcess())
{
    Line("");
    Line($"- 작업 집합 메모리 최대값: {proc.PeakWorkingSet64 / 1024 / 1024} MB");
}

File.WriteAllText(Path.Combine(outDir, "s4-results.md"), report.ToString());
Console.WriteLine($"\n결과 저장: {Path.Combine(outDir, "s4-results.md")}");

static bool IsDebug()
{
#if DEBUG
    return true;
#else
    return false;
#endif
}

internal record SizeInfo(double W, double H);
