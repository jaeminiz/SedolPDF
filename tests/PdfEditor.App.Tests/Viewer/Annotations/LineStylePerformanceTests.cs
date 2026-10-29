using System.Diagnostics;
using Pdf.Native;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Appearance;
using PdfEditor.Document.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Annotations;

/// <summary>
/// 0.4.0 Task 2·3 성능: 선 모양(대시)·둘째 지시선이 Painter → 화면 항목(AppearanceGeometry.FromDrawing) 변환과 그리기 패스 비용을 늘리지 않는다.
/// 주석 1000개(점선·파선·일점쇄선·양쪽 화살표)를 처음 그릴 때(캐시 만들기)와 캐시된 항목을 그릴 때(헤드리스 Skia 비트맵)를 잰다.
/// 수치는 임시 폴더 v040-linestyle-perf.txt.
/// </summary>
public class LineStylePerformanceTests
{
    private static readonly string Report = Path.Combine(Path.GetTempPath(), "v040-linestyle-perf.txt");

    private static List<Annotation> Lines(bool styled)
    {
        var styles = new[] { LineStyle.Solid, LineStyle.Dotted, LineStyle.Dashed, LineStyle.DashDot };
        var lines = new List<Annotation>();
        for (int i = 0; i < 1000; i++)
        {
            float y = 20 + i % 700;
            lines.Add(new Annotation
            {
                Kind = i % 3 == 0 ? AnnotKind.PolyLine : AnnotKind.Line, Color = new PdfColor(200, 30, 30), BorderWidth = 1 + i % 4,
                LineStyle = styled ? styles[i % 4] : LineStyle.Solid,
                StartEnding = i % 2 == 0 ? LineEnding.ClosedArrow : LineEnding.None, EndEnding = LineEnding.OpenArrow,
                Vertices = [new PdfPointF(30, y), new PdfPointF(200 + i % 100, y + 10), new PdfPointF(400, y)],
            });
        }
        return lines;
    }

    [Fact]
    public void Painter_ThousandStyledLines_ConversionIsFast() => HeadlessUi.Run(() =>
    {
        var lines = Lines(styled: true);
        for (int i = 0; i < 50; i++) AppearanceGeometry.FromDrawing(AnnotationPainter.Paint(lines[i], 0));   // 워밍업

        var times = new double[1000];
        for (int i = 0; i < lines.Count; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var c = AppearanceGeometry.FromDrawing(AnnotationPainter.Paint(lines[i], 0));
            times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Assert.NotEmpty(c.Items);
        }
        Array.Sort(times);
        double total = times.Sum(), p95 = times[(int)(times.Length * 0.95)], max = times[^1];

        double Avg(LineStyle s)
        {
            var sub = lines.Where(a => a.LineStyle == s).Take(100).ToList();
            long t0 = Stopwatch.GetTimestamp();
            for (int r = 0; r < 5; r++) foreach (var a in sub) AppearanceGeometry.FromDrawing(AnnotationPainter.Paint(a, 0));
            return Stopwatch.GetElapsedTime(t0).TotalMilliseconds / (5 * sub.Count);
        }
        double solid = Avg(LineStyle.Solid), dashed = Avg(LineStyle.Dashed), dotted = Avg(LineStyle.Dotted), dd = Avg(LineStyle.DashDot);

        long w0 = Stopwatch.GetTimestamp();
        foreach (var a in lines) PdfContentWriter.Write(AnnotationPainter.Paint(a, 0));
        double writeTotal = Stopwatch.GetElapsedTime(w0).TotalMilliseconds;

        File.WriteAllText(Report,
            $"1000 styled lines Paint+FromDrawing: total {total:F1} ms, p95 {p95:F3} ms, max {max:F3} ms\n" +
            $"avg per annotation: solid {solid:F4} ms, dashed {dashed:F4} ms, dotted {dotted:F4} ms, dash-dot {dd:F4} ms\n" +
            $"1000 AP content streams (Paint+Write): {writeTotal:F1} ms\n");

        Assert.True(p95 < 1.0, $"p95 {p95:F3} ms");   // 한 주석의 그리기 항목 만들기 p95 1 ms 미만
        Assert.True(total < 400, $"total {total:F1} ms");
        Assert.True(dashed < solid * 3 + 0.02, $"dashed {dashed:F4} vs solid {solid:F4}");   // 대시가 비용을 몇 배로 늘리지 않는다
        return 0;
    });

    [Fact]
    public void DrawPass_ThousandCachedAnnotations_DashedPensDoNotSlowDown()
    {
        var (plain, styled) = HeadlessUi.Run(() => (
            Lines(false).Select(a => AppearanceGeometry.FromDrawing(AnnotationPainter.Paint(a, 0))).ToList(),
            Lines(true).Select(a => AppearanceGeometry.FromDrawing(AnnotationPainter.Paint(a, 0))).ToList()));

        double DrawP95(List<CachedAppearance> set)
        {
            var ts = new double[15];
            for (int r = 0; r < ts.Length; r++)
            {
                long t0 = Stopwatch.GetTimestamp();
                HeadlessUi.RenderBgra(600, 800, ctx => { foreach (var c in set) foreach (var (g, f, p) in c.Items) ctx.DrawGeometry(f, p, g); });
                ts[r] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            Array.Sort(ts);
            return ts[13];   // 15개 중 14번째 ≈ p95
        }
        DrawP95(plain);   // 워밍업
        double drawPlain = DrawP95(plain), drawStyled = DrawP95(styled);
        File.AppendAllText(Report, $"page draw pass, 1000 cached annotations (600x800 headless Skia, p95 of 15): solid {drawPlain:F1} ms, styled {drawStyled:F1} ms\n");
        Assert.True(drawStyled < drawPlain * 3 + 30, $"styled {drawStyled:F1} ms vs solid {drawPlain:F1} ms");
    }
}
