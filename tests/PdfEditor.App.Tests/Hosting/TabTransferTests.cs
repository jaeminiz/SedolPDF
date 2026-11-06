using PdfEditor.App.Hosting;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Documents;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;
using Xunit;
using static PdfEditor.App.Tests.Hosting.AppHostWindowsTests;

namespace PdfEditor.App.Tests.Hosting;

/// <summary>0.8.0 단계 3: 탭을 새 창·다른 창으로 옮긴다(헤드리스 창).</summary>
public sealed class TabTransferTests : IDisposable
{
    private readonly HostTestScope _scope = new();

    public void Dispose() => _scope.Dispose();

    private static void Wait(Task t) { PumpUntil(() => t.IsCompleted); t.GetAwaiter().GetResult(); }

    private string Pdf(string name, int pages = 1)
    {
        string p = _scope.File(name);
        TestPdfFactory.CreateBlank(p, pages, 595, 842);
        return p;
    }

    private static MainWindow WithFiles(AppHost host, MainWindow? copyFrom, params string[] files)
    {
        var w = host.CreateWindow(copyFrom);
        foreach (var f in files) Wait(w.ViewModel.OpenAsync(f));
        return w;
    }

    [Fact]
    public void MoveToNewWindow_CreatesWindowWithTab()
    {
        string a = Pdf("a.pdf"), b = Pdf("b.pdf");
        Run(host =>
        {
            var w1 = WithFiles(host, null, a, b);
            var tab = w1.ViewModel.Documents[1];
            var s = tab.Session;
            Assert.True(host.Transfer.MoveToNewWindow(w1, tab));
            Assert.Equal(2, host.Registry.Count);
            var w2 = (MainWindow)host.Registry.LastActive!;
            Assert.NotSame(w1, w2);
            Assert.Same(s, Assert.Single(w2.ViewModel.Documents).Session);
            Assert.Single(w1.ViewModel.Documents);
        });
    }

    [Fact]
    public void MoveToNewWindow_SingleTab_IsNoOp()
    {
        string a = Pdf("a.pdf");
        Run(host =>
        {
            var w1 = WithFiles(host, null, a);
            Assert.False(host.Transfer.MoveToNewWindow(w1, w1.ViewModel.Documents[0]));
            Assert.Equal(1, host.Registry.Count);
            Assert.Single(w1.ViewModel.Documents);
        });
    }

    [Fact]
    public void MoveToWindow_LastTab_ClosesSource()
    {
        string a = Pdf("a.pdf"), b = Pdf("b.pdf");
        Run(host =>
        {
            var w1 = WithFiles(host, null, a);
            var w2 = WithFiles(host, w1, b);
            var s = w1.ViewModel.Documents[0].Session;
            Assert.True(host.Transfer.MoveToWindow(w1, w1.ViewModel.Documents[0], w2, 99));
            PumpUntil(() => w1.IsClosed);
            Assert.Equal(1, host.Registry.Count);
            Assert.Equal(2, w2.ViewModel.Documents.Count);
            Assert.Same(s, w2.ViewModel.ActiveDocument!.Session);
            Assert.Same(w2, host.Registry.LastActive);
            Assert.False(host.IsDisposed);
        });
    }

    [Fact]
    public void MoveToWindow_InsertsAtSlot()
    {
        string a = Pdf("a.pdf"), b = Pdf("b.pdf"), c = Pdf("c.pdf"), d = Pdf("d.pdf");
        Run(host =>
        {
            var w1 = WithFiles(host, null, a, b);
            var w2 = WithFiles(host, w1, c, d);
            var s = w1.ViewModel.Documents[0].Session;
            Assert.True(host.Transfer.MoveToWindow(w1, w1.ViewModel.Documents[0], w2, 1));
            Assert.Equal(3, w2.ViewModel.Documents.Count);
            Assert.Same(s, w2.ViewModel.Documents[1].Session);
            Assert.Single(w1.ViewModel.Documents);
            Assert.False(w1.IsClosed);
        });
    }

    [Fact]
    public void MoveToWindow_SameWindow_Reorders()
    {
        string a = Pdf("a.pdf"), b = Pdf("b.pdf");
        Run(host =>
        {
            var w1 = WithFiles(host, null, a, b);
            var first = w1.ViewModel.Documents[0];
            Assert.True(host.Transfer.MoveToWindow(w1, first, w1, 2));
            Assert.Same(first, w1.ViewModel.Documents[1]);
        });
    }

    [Fact]
    public void Move_BusySession_IsRefusedWithNotice()
    {
        string a = Pdf("a.pdf"), b = Pdf("b.pdf");
        var prompts = new FakePrompts();
        Run(host =>
        {
            var w1 = WithFiles(host, null, a, b);
            var tab = w1.ViewModel.Documents[0];
            using (host.Workflow.MarkBusy(tab.Session!))
                Assert.False(host.Transfer.MoveToNewWindow(w1, tab));
            Assert.Equal(1, host.Registry.Count);
            Assert.Equal(2, w1.ViewModel.Documents.Count);
            Assert.Contains(PdfEditor.App.Resources.Strings.Msg_TabBusyCannotMove, prompts.Infos);
        }, prompts);
    }

    [Fact]
    public void Recovery_FileSurvivesTransfer()
    {
        string a = Pdf("a.pdf", 3), b = Pdf("b.pdf");
        string saveTo = _scope.File("saved.pdf");
        var prompts = new FakePrompts();
        prompts.SavePicks.Enqueue(saveTo);
        Run(host =>
        {
            var w1 = WithFiles(host, null, a, b);
            var w2 = WithFiles(host, w1, Pdf("c.pdf"));
            var s = w1.ViewModel.Documents[0].Session!;
            Wait(s.Commands.ExecuteAsync(new DeletePagesCommand([s.Pages[1].Id])));
            var written = host.Recovery.WriteDirtyAsync([s]);
            Wait(written);
            Assert.Equal(1, written.Result);
            bool HasFile() => Directory.Exists(_scope.RecoveryDir) && Directory.EnumerateFiles(_scope.RecoveryDir, s.Id.ToString("N") + ".*").Any();
            Assert.True(HasFile());

            Assert.True(host.Transfer.MoveToWindow(w1, w1.ViewModel.Documents[0], w2, 0));
            Assert.True(HasFile());
            Assert.Contains(s, host.Workflow.OpenSessions);

            var saved = host.Workflow.SaveAsAsync(s);
            Wait(saved);
            Assert.True(saved.Result);
            Assert.False(HasFile());
        }, prompts);
    }
}
