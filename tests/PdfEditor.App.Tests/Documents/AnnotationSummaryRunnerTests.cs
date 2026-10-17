using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Panels;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEditor.App.Tests.Documents;

/// <summary>주석 요약 PDF 흐름 (M3b Task 7): 주석 있는 쪽만, 번호 이어짐, 쪽 그림은 패치된 사본(선이 보임), 취소하면 파일 없음.</summary>
public class AnnotationSummaryRunnerTests : PanelTestBase
{
    private static AppearanceServices Services()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppearanceServices.Create(dir!.FullName);
    }

    private async Task<DocumentTabViewModel> OpenThreePages()
    {
        string p = Path.Combine(Dir, "doc.pdf");
        TestPdfFactory.CreateLabeled(p, [(400, 600), (400, 600), (400, 600)]);
        var tab = await OpenTab(p);
        await tab.Session!.AnnotationsReady;
        var s = tab.Session;
        var T = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        // 1쪽: 메모가 있는 사각형(PDFium 길), 2쪽: 선(패치 길 — 편집 중 PDFium 문서에는 없음), 3쪽: 없음
        await tab.RunAnnotationCommandAsync(new ChangeAnnotationsCommand(s.Pages[0].Id, [new AnnotationChange(null, new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(50, 300, 200, 400), Color = new PdfColor(255, 0, 0),
            BorderWidth = 4, Author = "김재민", Contents = "도면 번호 확인", CreatedAt = T, ModifiedAt = T, Flags = PdfAnnotFlags.Print,
        })]));
        await tab.RunAnnotationCommandAsync(new ChangeAnnotationsCommand(s.Pages[1].Id, [new AnnotationChange(null, new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Line, Rect = new PdfRectF(50, 100, 350, 500), Color = new PdfColor(255, 0, 0),
            BorderWidth = 6, Vertices = [new PdfPointF(50, 100), new PdfPointF(350, 500)], Author = "Lee", Contents = "선 메모",
            CreatedAt = T, ModifiedAt = T, Flags = PdfAnnotFlags.Print,
        })]));
        return tab;
    }

    private static int ReddishPixels(string pdf, int pageIndex)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var res = (PdfDictionary)doc.Pages[pageIndex].Elements.GetObject("/Resources")!;
        var xo = (PdfDictionary)res.Elements.GetObject("/XObject")!;
        var img = (PdfDictionary)xo.Elements.GetObject(xo.Elements.Keys.First())!;
        var raw = img.Stream.UnfilteredValue;
        int n = 0;
        for (int i = 0; i + 2 < raw.Length; i += 3)
            if (raw[i] > 150 && raw[i + 1] < 90 && raw[i + 2] < 90) n++;
        return n;
    }

    [Fact]
    public async Task Summary_OnlyAnnotatedPages_PatchedCopyShowsLine()
    {
        var tab = await OpenThreePages();
        var svc = Services();
        var plans = AnnotationSummaryRunner.Collect(tab);
        Assert.Equal([0, 1], plans.Select(p => p.PageIndex).ToArray());

        string outPath = Path.Combine(Dir, "sum.pdf");
        var done = new List<int>();
        var outcome = await AnnotationSummaryRunner.RunAsync(tab, svc, plans, outPath, new Progress<int>(done.Add), CancellationToken.None);

        Assert.Equal(2, outcome.AnnotationCount);
        Assert.Equal(0, outcome.MissingGlyphs);
        using var doc = PdfReader.Open(outPath, PdfDocumentOpenMode.Import);
        Assert.Equal(outcome.SummaryPages, doc.PageCount);
        Assert.True(ReddishPixels(outPath, 0) > 50, "page 1 image should show the square");
        string text = await ExtractText(outPath);
        Assert.Contains(string.Format(Strings.Summary_PageHeadingFormat, 2), text);
        Assert.DoesNotContain(string.Format(Strings.Summary_PageHeadingFormat, 3), text);
        Assert.Contains("선 메모", text.Replace(" ", "").Replace("\r", "").Replace("\n", "").Replace("선메모", "선 메모"));
    }

    [Fact]
    public async Task Summary_LinePageImage_ContainsLine()
    {
        var tab = await OpenThreePages();
        var plans = AnnotationSummaryRunner.Collect(tab);
        string outPath = Path.Combine(Dir, "sum2.pdf");
        await AnnotationSummaryRunner.RunAsync(tab, Services(), [plans[1]], outPath, null, CancellationToken.None);
        Assert.True(ReddishPixels(outPath, 0) > 100, "line must be rendered from the patched copy");
    }

    [Fact]
    public async Task Summary_Cancelled_NoFileAndNoTempLeft()
    {
        var tab = await OpenThreePages();
        var plans = AnnotationSummaryRunner.Collect(tab);
        string outPath = Path.Combine(Dir, "sum3.pdf");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AnnotationSummaryRunner.RunAsync(tab, Services(), plans, outPath, null, cts.Token));
        Assert.False(File.Exists(outPath));
        Assert.Empty(Directory.GetFiles(Dir, "*.tmp"));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "~sedol-summary-*"));
    }

    [Fact]
    public async Task Command_NoAnnotations_InformsAndAsksNothing()
    {
        string p = Path.Combine(Dir, "empty.pdf");
        TestPdfFactory.CreateLabeled(p, [(400, 600)]);
        var tab = await OpenTab(p);
        var prompts = new FakePrompts { SummaryPick = Path.Combine(Dir, "x.pdf") };
        var vm = new MainWindowViewModel();
        vm.AttachWorkflow(null!, prompts, a => a());
        await vm.SummaryAsync(tab);
        Assert.Equal([Strings.Msg_SummaryNoAnnotations], prompts.Infos);
        Assert.Empty(prompts.SummarySuggested);
        Assert.Equal(0, prompts.ProgressRuns);
    }

    [Fact]
    public async Task Command_AsksLocationWithDefaultName_AndWritesFile()
    {
        var tab = await OpenThreePages();
        string outPath = Path.Combine(Dir, "chosen.pdf");
        var prompts = new FakePrompts { SummaryPick = outPath };
        var saved = DocumentTabViewModel.Appearance;
        DocumentTabViewModel.Appearance = Services();
        try
        {
            var vm = new MainWindowViewModel();
            vm.AttachWorkflow(null!, prompts, a => a());
            await vm.SummaryAsync(tab);
        }
        finally { DocumentTabViewModel.Appearance = saved; }
        Assert.Equal([Path.GetFileNameWithoutExtension(tab.Session!.DisplayName) + "-summary.pdf"], prompts.SummarySuggested);
        Assert.Equal(1, prompts.ProgressRuns);
        Assert.True(File.Exists(outPath));
        Assert.Empty(prompts.Errors);
        Assert.Single(prompts.Infos);
    }

    private async Task<string> ExtractText(string pdf)
    {
        using var t = new PdfiumThread();
        return await t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(t, pdf);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < d.PageCount; i++)
            {
                using var pg = d.LoadPage(i);
                using var tp = pg.LoadText();
                sb.Append(tp.GetText(0, tp.CharCount));
            }
            return sb.ToString();
        });
    }
}
