using Avalonia.Threading;
using PdfEditor.App.Hosting;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.I18n;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.TestFixtures;
using Xunit;

namespace PdfEditor.App.Tests.Hosting;

/// <summary>여러 창 기반(0.8.0 단계 2): 새 창, 대화상자 부모, 외부 파일 열기. 헤드리스 창으로 시험한다.</summary>
public sealed class AppHostWindowsTests : IDisposable
{
    private readonly HostTestScope _scope = new();

    public void Dispose() => _scope.Dispose();

    /// <summary>UI 일을 돌리며 조건이 될 때까지(최대 maxMs) 기다린다.</summary>
    internal static void PumpUntil(Func<bool> done, int maxMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return;
            Thread.Sleep(15);
        }
        throw new TimeoutException("조건이 제때 되지 않음");
    }

    internal static void Run(Action<AppHost> body, PdfEditor.App.Documents.IUserPrompts? prompts = null)
    {
        HeadlessUi.Run(() =>
        {
            using var styles = new EnglishLayoutTests.AppStyles();
            var host = new AppHost(AppContext.BaseDirectory, prompts);
            try { body(host); }
            finally
            {
                if (prompts is PdfEditor.App.Tests.Documents.FakePrompts fake) fake.SaveChoice = PdfEditor.App.Documents.UnsavedChoice.Discard;   // 정리할 때는 버리고 닫는다
                var open = host.Registry.ByRecent.OfType<MainWindow>().ToList();
                foreach (var w in open) w.Close();
                PumpUntil(() => open.All(w => w.IsClosed));
                host.Dispose();
            }
            return 0;
        });
    }

    [Fact]
    public void NewWindow_CopiesPanelState()
    {
        Run(host =>
        {
            var w1 = host.CreateWindow(null);
            w1.ViewModel.SelectRibbonTabCommand.Execute("Page");
            w1.ViewModel.SetLeftPanelWidth(333);
            var w2 = host.CreateWindow(w1);
            Assert.Equal("Page", w2.ViewModel.SelectedRibbonTab);
            Assert.Equal(w1.ViewModel.LeftPanelWidth, w2.ViewModel.LeftPanelWidth);
            Assert.Equal(w1.ViewModel.IsLeftPanelVisible, w2.ViewModel.IsLeftPanelVisible);
            Assert.Equal(2, host.Registry.Count);
            Assert.Same(w2, host.Registry.LastActive);
        });
    }

    [Fact]
    public void DialogOwner_SkipsClosedWindow()
    {
        Run(host =>
        {
            var w1 = host.CreateWindow(null);
            var w2 = host.CreateWindow(w1);
            Assert.Same(w2, host.Registry.LastActive);
            w2.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(w2.IsClosed);
            _ = host.Prompts.ShowInfoAsync("x");
            Dispatcher.UIThread.RunJobs();
            Assert.Single(w1.OwnedWindows);
            foreach (var d in w1.OwnedWindows.ToList()) d.Close();
        });
    }

    [Fact]
    public void OpenAsync_SessionOwnedByOtherWindow_RevealsThatWindow()
    {
        string a = _scope.File("a.pdf");
        TestPdfFactory.CreateBlank(a, 1, 595, 842);
        Run(host =>
        {
            var w1 = host.CreateWindow(null);
            var t = w1.ViewModel.OpenAsync(a);
            PumpUntil(() => t.IsCompleted);
            var session = w1.ViewModel.ActiveDocument!.Session;
            var w2 = host.CreateWindow(w1);
            Assert.Same(w2, host.Registry.LastActive);
            t = w2.ViewModel.OpenAsync(a);
            PumpUntil(() => t.IsCompleted);
            t.GetAwaiter().GetResult();
            Assert.Empty(w2.ViewModel.Documents);
            Assert.Single(w1.ViewModel.Documents);
            Assert.Same(session, w1.ViewModel.ActiveDocument!.Session);
            Assert.Same(w1, host.Registry.LastActive);
        });
    }

    [Fact]
    public void OpenExternal_GoesToLastActiveWindow()
    {
        string a = _scope.File("a.pdf");
        string b = _scope.File("b.pdf");
        TestPdfFactory.CreateBlank(a, 1, 595, 842);
        TestPdfFactory.CreateBlank(b, 1, 595, 842);
        Run(host =>
        {
            var w1 = host.CreateWindow(null);
            var w2 = host.CreateWindow(w1);
            var t = host.OpenExternalAsync([a]);
            PumpUntil(() => t.IsCompleted);
            t.GetAwaiter().GetResult();
            Assert.Single(w2.ViewModel.Documents);
            Assert.Empty(w1.ViewModel.Documents);

            host.Registry.MarkActivated(w1);
            t = host.OpenExternalAsync([b]);
            PumpUntil(() => t.IsCompleted);
            t.GetAwaiter().GetResult();
            Assert.Single(w1.ViewModel.Documents);
        });
    }
}
