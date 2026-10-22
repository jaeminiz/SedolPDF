using System.Diagnostics;
using System.Text;
using Pdf.Native;
using PdfEditor.ContentStream;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Tests;

// V21: 클릭 → 문단 상자까지 단계별 시간(Release 권장). V17ToV21Probes의 나머지 절반.
public sealed partial class V17ToV21Probes
{
    private sealed record ClickTiming(double SelectMs, double LoadTextMs, double ReadMs, double ObstacleMs, double DetectMs, int Candidates, int Runs, int Segs, int GroupLines);

    /// <summary>
    /// 한 쪽에서 글 객체 3곳(25·50·75 % 지점)을 클릭했다고 보고 PDFium 스레드에서 후보 글 읽기까지를 3회 잰다.
    /// 후보 = 클릭 줄 주변(세로 ±60 줄 크기, 가로 ±(쪽 폭 20 %) 안)의 글 객체 중 가까운 60개.
    /// </summary>
    private (List<ClickTiming> Clicks, double OpenMs, double IndexMs, int Objects, int TextObjects) MeasureClicks(string path, int pageIndex)
    {
        return _t.InvokeAsync(() =>
        {
            var sw = Stopwatch.StartNew();
            using var d = Pdf.Native.PdfDocument.Open(_t, path, null);
            using var p = d.LoadPage(pageIndex);
            double openMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            // (기존 PageObjectIndex가 이미 하는 일) 글·경로 객체의 경계 목록
            int n = p.ObjectCount;
            var texts = new List<(int I, PdfRectF B)>();
            var paths = new List<(int I, PdfRectF B)>();
            for (int i = 0; i < n; i++)
            {
                var o = p.GetObject(i);
                var type = o.Type;
                if (type == PdfPageObjectType.Text) texts.Add((i, o.Bounds));
                else if (type == PdfPageObjectType.Path) paths.Add((i, o.Bounds));
            }
            double indexMs = sw.Elapsed.TotalMilliseconds;
            var clicks = new List<ClickTiming>();
            if (texts.Count == 0) return (clicks, openMs, indexMs, n, 0);
            var sz = p.SizePt;
            foreach (int rep in new[] { 0, 1, 2 })
                foreach (double frac in new[] { 0.25, 0.5, 0.75 })
                {
                    var seed = texts[(int)(frac * (texts.Count - 1))];
                    double h = Math.Max(4, seed.B.Height);
                    sw.Restart();
                    var cand = texts.Where(t => Math.Abs((t.B.Bottom + t.B.Top) / 2 - (seed.B.Bottom + seed.B.Top) / 2) <= 60 * h
                                             && t.B.Right >= seed.B.Left - 0.2 * sz.Width && t.B.Left <= seed.B.Right + 0.2 * sz.Width)
                        .OrderBy(t => Math.Abs((t.B.Bottom + t.B.Top) / 2 - (seed.B.Bottom + seed.B.Top) / 2)).Take(60).ToList();
                    double selMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    using var tp = p.LoadText();
                    double ltMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    var runs = new List<Run>();
                    foreach (var (i, _) in cand)
                    {
                        var o = p.GetObject(i);
                        string? text = o.GetText(tp);
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        var r = ParagraphProbe.ReadRun(i, o, text);
                        if (r.Run is not null && r.Visible) runs.Add(r.Run);
                    }
                    double readMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    var window = new PdfRectF(cand.Count == 0 ? 0 : cand.Min(c => c.B.Left) - 5, cand.Count == 0 ? 0 : cand.Min(c => c.B.Bottom) - 5,
                                              cand.Count == 0 ? 0 : cand.Max(c => c.B.Right) + 5, cand.Count == 0 ? 0 : cand.Max(c => c.B.Top) + 5);
                    var pd = new PageData { W = sz.Width, H = sz.Height };
                    foreach (var (i, b) in paths)
                    {
                        if (b.Right < window.Left || b.Left > window.Right || b.Top < window.Bottom || b.Bottom > window.Top) continue;
                        ParagraphProbe.AddPathObstacles(p.GetObject(i), pd);
                    }
                    double obMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    var segs = ParagraphProbe.Assemble(runs, pd.Obstacles);
                    var page = new ParagraphProbe.Page(segs, pd.Obstacles);
                    int groupLines = 0;
                    if (segs.Count > 0)
                    {
                        var seedSeg = segs.OrderBy(s => Math.Abs(s.U0 - seed.B.Left) + Math.Abs(s.V - seed.B.Bottom)).First();
                        groupLines = ParagraphProbe.Detect(page, seedSeg).Lines.Count;
                    }
                    double detMs = sw.Elapsed.TotalMilliseconds;
                    clicks.Add(new ClickTiming(selMs, ltMs, readMs, obMs, detMs, cand.Count, runs.Count, segs.Count, groupLines));
                }
            return (clicks, openMs, indexMs, n, texts.Count);
        }).GetAwaiter().GetResult();
    }

    private static string Fmt(IEnumerable<double> xs) { var a = xs.OrderBy(x => x).ToList(); return a.Count == 0 ? "-" : $"중앙 {a[a.Count / 2]:0.00} / 최대 {a[^1]:0.00}"; }

    [Fact]
    public void V21_ClickToParagraphBox_Timings()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"== V21 클릭 → 문단 상자까지 단계별 시간 (ms). 빌드: {(IsRelease ? "Release" : "Debug — 수치는 Release보다 느림")}. 같은 클릭 3곳 × 3회");
        string? a1 = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "spikes", "out", "bench-a1-drawing.pdf");
            if (File.Exists(p)) { a1 = p; break; }
        }
        var targets = new List<(string Label, string Path, int Page)>();
        if (a1 is not null) targets.Add(("A1 도면(bench-a1-drawing, 5,000줄·205,000 객체)", a1, 0));
        targets.Add(("합성 문서(doc-en-justified)", Samples().First(s => s.Name == "doc-en-justified").Path, 0));
        foreach (var s in RealSources()) targets.Add(($"{s.Label} 3쪽", s.Path, 2));
        foreach (var (label, path, page) in targets)
        {
            var (clicks, openMs, indexMs, objs, texts) = MeasureClicks(path, page);
            sb.AppendLine($"-- {label}: 객체 {objs}, 글 객체 {texts}");
            sb.AppendLine($"   (참고: 이미 앱이 치르는 비용) 쪽 열기 {openMs:0} ms, 객체 경계 목록 만들기(PageObjectIndex 상당) {indexMs:0} ms");
            if (clicks.Count == 0) { sb.AppendLine("   글 객체 없음"); continue; }
            sb.AppendLine($"   ① 후보 고르기 {Fmt(clicks.Select(c => c.SelectMs))} | 글 쪽 열기(LoadText) {Fmt(clicks.Select(c => c.LoadTextMs))} | 후보 글 읽기(≤60) {Fmt(clicks.Select(c => c.ReadMs))} | 장애물(선·경로) 읽기 {Fmt(clicks.Select(c => c.ObstacleMs))}");
            sb.AppendLine($"   ② 줄 조립 + 문단 묶기 {Fmt(clicks.Select(c => c.DetectMs))} (후보 {clicks.Average(c => c.Candidates):0}개 → 글 객체 {clicks.Average(c => c.Runs):0} → 줄 {clicks.Average(c => c.Segs):0}, 묶인 줄 수 {string.Join("/", clicks.Take(3).Select(c => c.GroupLines))})");
            var total = clicks.Select(c => c.SelectMs + c.LoadTextMs + c.ReadMs + c.ObstacleMs + c.DetectMs).ToList();
            sb.AppendLine($"   ①+②(PDFium 스레드 몫) 합계: {Fmt(total)}  [목표: 도면 ≤ 100 ms, 문서 ≤ 30 ms — 코드북·레이아웃은 아래 별도]");
        }
        // ③ 코드북: 쪽 내용 해석(쪽마다 한 번, 캐시 가능) + 글꼴 코드북
        sb.AppendLine();
        sb.AppendLine("-- ③ 코드북 재료: 쪽 내용 풀기·해석 + 글꼴 코드북 + ④ 레이아웃(재배치 계산) 시간 (PDFsharp 쪽, PDFium 스레드 밖)");
        foreach (var (label, path, page) in targets)
        {
            var sw = Stopwatch.StartNew();
            using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            double openDoc = sw.Elapsed.TotalMilliseconds;
            var times = new List<double>();
            int objCount = 0;
            for (int rep = 0; rep < 3; rep++)
            {
                var pd = ParagraphProbe.ReadPdfium(_t, path, page);   // 종류 열 확보용(앱에서는 이미 있는 목록) — 시간에 안 넣음
                sw.Restart();
                var map = StreamMap.Build(doc.Pages[page], pd.Kinds, out _, out double parseMs, out objCount);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            // 글꼴 코드북 + 레이아웃(재배치) — 클릭 줄 주변 문단 하나
            var study = ProbeStudy.Study(_t, label, path, 1, page).First();
            var books = new ProbeStudy.Books();
            var groups = ParagraphProbe.Partition(study.Page, Tc4).Where(g => g.Lines.Count >= 2).ToList();
            double bookMs = 0, layoutMs = 0;
            if (groups.Count > 0)
            {
                var g = groups[0];
                sw.Restart();
                foreach (var seg in g.Lines) foreach (var run in seg.Runs) if (study.RunStream.TryGetValue(run, out var ts)) _ = ProbeStudy.Books.Fresh(ts.Font);
                bookMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                for (int i = 0; i < 20; i++) _ = ProbeStudy.Reproduce(study, g, new ProbeStudy.Books(), false);
                layoutMs = sw.Elapsed.TotalMilliseconds / 20;
            }
            sb.AppendLine($"{label}: PDFsharp 문서 열기 {openDoc:0} ms (한 번) | 쪽 내용 풀기·해석(객체 {objCount}개) {Fmt(times)} ms | 문단 하나의 글꼴 코드북 {bookMs:0.0} ms | 재배치 계산(코드북 포함 폭 재기 + 줄바꿈, 문단 {(groups.Count > 0 ? groups[0].Lines.Count : 0)}줄) {layoutMs:0.00} ms");
        }
        File.WriteAllText(Out("m5-v21.txt"), sb.ToString());
    }

    private static bool IsRelease =>
#if DEBUG
        false;
#else
        true;
#endif
}
