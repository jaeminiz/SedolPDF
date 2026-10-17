using System.Diagnostics;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;
using Xunit.Abstractions;

namespace PdfEditor.App.Tests.Panels;

/// <summary>
/// 주석 2,000개(20쪽 × 쪽당 최상위 80 + 답글 20) 문서에서 코멘트 목록 모델의 만들기·거르기·정렬·한 쪽 다시 만들기 시간 (M3b Task 8, 화면 없이 모델만).
/// 수치는 출력으로 남기고(결과 문서에 적는다) 여기서는 느슨한 상한만 확인한다. Release 빌드에서 재면 결과 문서의 값이 나온다.
/// </summary>
public class CommentsViewModel2000Tests(ITestOutputHelper output) : PanelTestBase
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Panel2000_BuildFilterSortRebuildTimings()
    {
        string path = Path.Combine(Dir, "big2000.pdf");
        TestPdfFactory.CreateLabeled(path, [.. Enumerable.Repeat((595, 842), 20)]);
        var tab = await OpenTab(path);
        var s = tab.Session!;
        await s.AnnotationsReady;
        for (int pg = 0; pg < 20; pg++)
        {
            var tops = new List<Annotation>();
            for (int i = 0; i < 80; i++)
            {
                float x = 20 + (i % 10) * 50, y = 40 + (i / 10) * 70;
                tops.Add(new Annotation
                {
                    Id = s.Annotations.NewId(), Kind = i % 3 == 0 ? AnnotKind.Circle : AnnotKind.Square, Rect = new PdfRectF(x, y, x + 30, y + 30),
                    Color = new PdfColor(200, 0, 0), Author = "author" + (i % 10), Contents = $"p{pg + 1} comment number {i}: long enough to wrap onto a second line",
                    CreatedAt = T0.AddMinutes(pg * 100 + i * 7 % 97), ModifiedAt = T0.AddMinutes(pg * 100 + i), Flags = PdfAnnotFlags.Print,
                });
            }
            await tab.RunAnnotationCommandAsync(new ChangeAnnotationsCommand(s.Pages[pg].Id, [.. tops.Select(a => new AnnotationChange(null, a))]));
            var replies = new List<Annotation>();
            for (int i = 0; i < 20; i++)
                replies.Add(ThreadAnnotations.NewReply(s.Annotations, tops[i * 4], $"reply {i} on p{pg + 1}", "reviewer" + (i % 3), T0.AddHours(2).AddMinutes(pg * 100 + i)));
            await tab.RunAnnotationCommandAsync(new ChangeAnnotationsCommand(s.Pages[pg].Id, [.. replies.Select(a => new AnnotationChange(null, a))]));
        }

        var sw = Stopwatch.StartNew();
        using var vm = new CommentsViewModel(tab);
        double buildMs = sw.Elapsed.TotalMilliseconds;
        Assert.Equal(2000, vm.TotalCount);
        output.WriteLine($"build(2000) = {buildMs:F1} ms (first run, includes JIT)");

        // 첫 호출(JIT)을 뺀 뒤, 연산마다 다섯 번의 최솟값·중앙값
        vm.SearchText = "warm"; vm.SearchText = "";
        var ops = new (string, Action, Action)[]
        {
            ("search 'number 12'", () => vm.SearchText = "number 12", () => vm.SearchText = ""),
            ("author filter", () => vm.AuthorFilter = "author3", () => vm.AuthorFilter = null),
            ("kind filter", () => vm.KindFilter = new HashSet<string> { Strings.AnnotKind_Circle }, () => vm.KindFilter = new HashSet<string>()),
            ("sort date", () => vm.SortBy = CommentSort.Date, () => vm.SortBy = CommentSort.Page),
            ("sort author", () => vm.SortBy = CommentSort.Author, () => vm.SortBy = CommentSort.Page),
            ("page range 5-8", () => vm.PageRange = "5-8", () => vm.PageRange = ""),
        };
        foreach (var (name, apply, undo) in ops)
        {
            var times = new List<double>();
            for (int r = 0; r < 5; r++)
            {
                var t = Stopwatch.StartNew();
                apply();
                t.Stop();
                times.Add(t.Elapsed.TotalMilliseconds);
                undo();
            }
            times.Sort();
            output.WriteLine($"{name}: min {times[0]:F2} ms, median {times[2]:F2} ms, max {times[4]:F2} ms");
            Assert.True(times[2] < 100, $"{name} median {times[2]:F1} ms");
        }
        Assert.Equal(2000, vm.ShownCount);

        // 한 쪽(주석 100개)만 다시 만들기: 새 주석 추가 → 변경 이벤트 → 그 쪽 줄만 갱신
        var rebuildTimes = new List<double>();
        for (int r = 0; r < 5; r++)
        {
            var add = new Annotation
            {
                Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(10, 10, 50, 40), Color = new PdfColor(0, 0, 200),
                Author = "late", Contents = "rebuild probe " + r, CreatedAt = T0.AddDays(1), ModifiedAt = T0.AddDays(1), Flags = PdfAnnotFlags.Print,
            };
            var t = Stopwatch.StartNew();
            await tab.RunAnnotationCommandAsync(new ChangeAnnotationsCommand(s.Pages[10].Id, [new AnnotationChange(null, add)]));
            t.Stop();
            rebuildTimes.Add(t.Elapsed.TotalMilliseconds);
        }
        output.WriteLine("one-page rebuild after add (command + store event + rows): " + string.Join(", ", rebuildTimes.Select(x => x.ToString("F1"))) + " ms");
        Assert.Equal(2005, vm.TotalCount);
        Assert.True(rebuildTimes.Min() < 250, "rebuild " + rebuildTimes.Min());
    }
}
