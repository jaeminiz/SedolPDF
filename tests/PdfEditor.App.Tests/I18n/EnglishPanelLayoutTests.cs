using Avalonia.Controls;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Tests.Panels;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;
using Xunit.Abstractions;

namespace PdfEditor.App.Tests.I18n;

/// <summary>
/// 영어 화면 패널 맞춤 점검 (0.4.0 Task 10 보강, 실제 화면 점검 지적 반영): 앱이 쓰는 패널 폭(찾아 바꾸기 340·코멘트 320·검색 200·속성 220)에서
/// 글자가 말줄임·잘림 없이 보이는지 영어·한국어 모두 확인한다.
/// </summary>
public class EnglishPanelLayoutTests(ITestOutputHelper output) : PanelTestBase
{
    private async Task<DocumentTabViewModel> OpenWithAnnotations()
    {
        string p = Path.Combine(Dir, "panel.pdf");
        TestPdfFactory.CreateLabeled(p, [(400, 600)]);
        var s = await PdfEditor.Document.DocumentSession.OpenAsync(Thread, p, null);
        await s.AnnotationsReady;
        var page = s.Pages[0].Id;
        Annotation Note(string text, float y) => new()
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(10, y, 50, y + 30), Color = new PdfColor(200, 0, 0),
            Author = "Reviewer Kim", Contents = text, CreatedAt = DateTimeOffset.Now, ModifiedAt = DateTimeOffset.Now, Flags = PdfAnnotFlags.Print,
        };
        await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(page,
            [new(null, Note("Please check the flange thickness against the approved drawing", 10)), new(null, Note("OK", 100))]));
        return new DocumentTabViewModel(s, "a.pdf", a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
    }

    private static List<string> Check(string lang, DocumentTabViewModel tab, Action<string, UserControl, double> addPanel)
    {
        var all = new List<string>();
        using var styles = new EnglishLayoutTests.AppStyles();
        using var _ = new CultureScope(lang);
        void One(string name, UserControl panel, double width)
        {
            var w = new Window { Width = width + 20, Height = 700, Content = new Border { Width = width, Child = panel } };
            w.Show();
            EnglishLayoutTests.Pump(w);
            EnglishLayoutTests.Save(w, $"{lang}-panel-{name}");
            all.AddRange(EnglishLayoutTests.Problems(w, $"{lang} {name}"));
            w.Close();
        }
        var replace = new FindReplaceViewModel(tab) { IsScopeRange = true, FindText = "H-2501", ReplaceText = "H-2502" };
        One("replace", new FindReplacePanel { DataContext = replace }, 340);
        using var comments = new CommentsViewModel(tab);
        One("comments", new CommentsPanel { DataContext = comments }, 320);
        One("search", new SearchPanel { DataContext = new SearchViewModel(tab) }, 200);
        One("properties", new PropertiesPanel { DataContext = new PropertiesViewModel(tab) }, 220);
        return all;
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    public async Task Panels_FitTheirWidths(string lang)
    {
        HeadlessUi.Run(() => 0);   // 헤드리스 UI 스레드를 가장 먼저 만든다(Dispatcher가 시험 스레드에 붙지 않게)
        var tab = await OpenWithAnnotations();
        var problems = HeadlessUi.Run(() => Check(lang, tab, (_, _, _) => { }));
        foreach (var p in problems) output.WriteLine(p);
        Assert.Empty(problems);
    }
}
