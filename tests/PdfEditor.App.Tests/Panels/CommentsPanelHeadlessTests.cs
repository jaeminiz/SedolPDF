using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

/// <summary>코멘트 패널 화면 묶음 (헤드리스): 목록이 모델과 맞게 그려지고, 답글 입력칸이 열리며, Enter로 답글이 달린다.</summary>
public class CommentsPanelHeadlessTests : PanelTestBase
{
    private static void Pump(Func<bool> done, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return;
            System.Threading.Thread.Sleep(15);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), "timed out");
    }

    [Fact]
    public async Task Panel_ShowsRows_ReplyBoxOpensAndEnterCommits()
    {
        string p = Path.Combine(Dir, "panel.pdf");
        TestPdfFactory.CreateLabeled(p, [(400, 600)]);
        var s = await PdfEditor.Document.DocumentSession.OpenAsync(Thread, p, null);
        await s.AnnotationsReady;
        var page = s.Pages[0].Id;
        Annotation Note(string text, float y) => new()
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(10, y, 50, y + 30), Color = new PdfColor(200, 0, 0),
            Author = "kim", Contents = text, CreatedAt = DateTimeOffset.Now, ModifiedAt = DateTimeOffset.Now, Flags = PdfAnnotFlags.Print,
        };
        await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(page, [new(null, Note("first", 10)), new(null, Note("second", 100))]));

        try
        {
            var result = HeadlessUi.Run(() =>
            {
                if (!Application.Current!.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any())
                    Application.Current.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
                var tab = new DocumentTabViewModel(s, "a.pdf", a => Dispatcher.UIThread.Post(a));
                var vm = new CommentsViewModel(tab);
                var panel = new CommentsPanel { DataContext = vm };
                var w = new Window { Width = 320, Height = 600, Content = panel };
                w.Show();
                Pump(() => { w.UpdateLayout(); return panel.GetVisualDescendants().OfType<ListBoxItem>().Count() == 2; });

                var texts = panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                var rows = vm.Items.ToList();
                bool contentsShown = texts.Contains("first") && texts.Contains("second");

                // 답글 입력칸: 처음에는 숨김, 답글 명령으로 열림
                TextBox Box(CommentItem item)
                {
                    var row = panel.GetVisualDescendants().OfType<ListBoxItem>().First(r => ReferenceEquals(r.DataContext, item));
                    return row.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "ReplyBox");
                }
                bool hiddenAtFirst = !Box(rows[0]).IsEffectivelyVisible;
                vm.ReplyCommand.Execute(rows[0]);
                w.UpdateLayout();
                var box = Box(rows[0]);
                bool opened = box.IsEffectivelyVisible && rows[0].Ui.IsReplying;

                box.Focus();
                box.Text = "typed reply";   // 양방향 연결 → Ui.Draft
                bool draftBound = rows[0].Ui.Draft == "typed reply";
                w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Pump(() => vm.Items.Count == 3);
                bool replyListed = vm.Items.Any(i => i.Depth == 1 && i.Contents == "typed reply");
                // 달린 뒤: 새 목록의 줄이 화면에 있고 입력칸은 닫혀 있다
                Pump(() => { w.UpdateLayout(); return panel.GetVisualDescendants().OfType<ListBoxItem>().Count() == 3; });
                bool anyOpen = panel.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "ReplyBox" && t.IsEffectivelyVisible);
                vm.Dispose();
                w.Close();
                return (contentsShown, hiddenAtFirst, opened, draftBound, replyListed, anyOpen);
            });
            Assert.True(result.contentsShown);
            Assert.True(result.hiddenAtFirst);
            Assert.True(result.opened);
            Assert.True(result.draftBound);
            Assert.True(result.replyListed);
            Assert.False(result.anyOpen);
        }
        finally { await s.DisposeAsync(); }
    }

    [Fact]
    public async Task ImeProcessedEnter_DoesNotCommit_PlainEnterDoes()
    {
        string p = Path.Combine(Dir, "ime.pdf");
        TestPdfFactory.CreateLabeled(p, [(400, 600)]);
        var s = await PdfEditor.Document.DocumentSession.OpenAsync(Thread, p, null);
        await s.AnnotationsReady;
        var page = s.Pages[0].Id;
        await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(page, [new(null, new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(10, 10, 50, 40), Color = new PdfColor(200, 0, 0),
            Author = "kim", Contents = "root", CreatedAt = DateTimeOffset.Now, ModifiedAt = DateTimeOffset.Now, Flags = PdfAnnotFlags.Print,
        })]));
        try
        {
            var r = HeadlessUi.Run(() =>
            {
                if (!Application.Current!.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any())
                    Application.Current.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
                var tab = new DocumentTabViewModel(s, "a.pdf", a => Dispatcher.UIThread.Post(a));
                var vm = new CommentsViewModel(tab);
                var panel = new CommentsPanel { DataContext = vm };
                var w = new Window { Width = 320, Height = 600, Content = panel };
                w.Show();
                Pump(() => { w.UpdateLayout(); return panel.GetVisualDescendants().OfType<ListBoxItem>().Any(); });
                var root = vm.Items[0];
                vm.ReplyCommand.Execute(root);
                w.UpdateLayout();
                var box = panel.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "ReplyBox");
                box.Focus();
                box.Text = "가나다";

                // IME가 먹은 Enter: 조합을 끝내는 키 → 답글을 달지 않는다
                w.KeyPress(Key.ImeProcessed, RawInputModifiers.None, PhysicalKey.Enter, null);
                for (int i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(10); }
                bool stillOpen = root.Ui.IsReplying && vm.Items.Count == 1;

                // 그다음 Enter가 확정
                w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Pump(() => vm.Items.Count == 2);
                bool committed = vm.Items.Any(i => i.Depth == 1 && i.Contents == "가나다");
                vm.Dispose();
                w.Close();
                return (stillOpen, committed);
            });
            Assert.True(r.stillOpen);
            Assert.True(r.committed);
        }
        finally { await s.DisposeAsync(); }
    }
}
