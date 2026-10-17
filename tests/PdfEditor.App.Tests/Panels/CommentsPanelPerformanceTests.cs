using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;
using Xunit.Abstractions;

namespace PdfEditor.App.Tests.Panels;

/// <summary>
/// 주석 2,000개 문서에서 코멘트 패널 (M3b Task 3 Step 4b): 목록이 가상화돼 줄을 2,000개 만들지 않고, 열기·스크롤이 한 프레임에 가깝다.
/// 시간은 헤드리스 소프트웨어 렌더 기준이라 실제 창과 다를 수 있다 — 수치는 결과 문서에 따로 적는다. 여기서는 느슨한 상한만 확인한다.
/// </summary>
public class CommentsPanelPerformanceTests(ITestOutputHelper output) : PanelTestBase
{
    private static void Pump(Func<bool> done, int ms = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return;
            System.Threading.Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), "timed out");
    }

    [Fact]
    public async Task Panel_With2000Annotations_IsVirtualized_ScrollsFast()
    {
        string p = Path.Combine(Dir, "big.pdf");
        TestPdfFactory.CreateLabeled(p, [.. Enumerable.Repeat((595, 842), 20)]);
        var s = await PdfEditor.Document.DocumentSession.OpenAsync(Thread, p, null);
        await s.AnnotationsReady;
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        for (int pg = 0; pg < 20; pg++)
        {
            var changes = new List<AnnotationChange>();
            for (int i = 0; i < 100; i++)
            {
                float x = 20 + (i % 10) * 50, y = 40 + (i / 10) * 70;
                changes.Add(new AnnotationChange(null, new Annotation
                {
                    Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(x, y, x + 30, y + 30), Color = new PdfColor(200, 0, 0),
                    Author = "kim", Contents = $"p{pg + 1} #{i}: a comment long enough to wrap onto a second line inside the list panel",
                    CreatedAt = t0.AddMinutes(pg * 100 + i), ModifiedAt = t0.AddMinutes(pg * 100 + i), Flags = PdfAnnotFlags.Print,
                }));
            }
            await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(s.Pages[pg].Id, changes));
        }

        try
        {
            var r = HeadlessUi.Run(() =>
            {
                if (!Application.Current!.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any())
                    Application.Current.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
                var tab = new DocumentTabViewModel(s, "big.pdf", a => Dispatcher.UIThread.Post(a));

                var sw = Stopwatch.StartNew();
                var vm = new CommentsViewModel(tab);
                double vmMs = sw.Elapsed.TotalMilliseconds;

                var panel = new CommentsPanel { DataContext = vm };
                var w = new Window { Width = 320, Height = 700, Content = panel };
                sw.Restart();
                w.Show();
                Pump(() => { w.UpdateLayout(); return panel.GetVisualDescendants().OfType<ListBoxItem>().Any(); });
                double openMs = sw.Elapsed.TotalMilliseconds;
                int realized = panel.GetVisualDescendants().OfType<ListBoxItem>().Count();

                var list = panel.GetVisualDescendants().OfType<ListBox>().First();
                var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
                var frames = new List<double>();
                for (int k = 1; k <= 30; k++)
                {
                    sw.Restart();
                    scroll.Offset = new Vector(0, k * 4000);
                    w.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    frames.Add(sw.Elapsed.TotalMilliseconds);
                }
                frames.Sort();
                int realizedAfter = panel.GetVisualDescendants().OfType<ListBoxItem>().Count();

                sw.Restart();
                vm.SearchText = "p7 #5";
                double filterMs = sw.Elapsed.TotalMilliseconds;
                int shown = vm.ShownCount;
                vm.Dispose();
                w.Close();
                return (vmMs, openMs, realized, realizedAfter, p50: frames[15], max: frames[^1], filterMs, shown, total: vm.TotalCount);
            });
            output.WriteLine($"2000 annotations: VM build {r.vmMs:F1} ms, panel open {r.openMs:F1} ms, realized rows {r.realized} (after scroll {r.realizedAfter}), " +
                             $"scroll frame p50 {r.p50:F1} ms / max {r.max:F1} ms, search filter {r.filterMs:F1} ms (shown {r.shown}/{r.total})");
            Assert.Equal(2000, r.total);
            Assert.True(r.realized < 100 && r.realizedAfter < 100, $"not virtualized: {r.realized}/{r.realizedAfter}");
            Assert.True(r.openMs < 3000, $"open {r.openMs:F0} ms");
            Assert.True(r.max < 1000, $"scroll max {r.max:F0} ms");
        }
        finally { await s.DisposeAsync(); }
    }
}
