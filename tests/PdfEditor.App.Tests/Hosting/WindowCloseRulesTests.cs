using PdfEditor.App.Documents;
using PdfEditor.App.Hosting;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Documents;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;
using Xunit;
using static PdfEditor.App.Tests.Hosting.AppHostWindowsTests;

namespace PdfEditor.App.Tests.Hosting;

/// <summary>창 닫기·끝내기·빈 창 규칙(0.8.0 단계 2, 설계 4.4 A안).</summary>
public sealed class WindowCloseRulesTests : IDisposable
{
    private readonly HostTestScope _scope = new();
    private readonly FakePrompts _prompts = new() { SaveChoice = UnsavedChoice.Cancel };

    public void Dispose() => _scope.Dispose();

    private static T Wait<T>(Task<T> t)
    {
        PumpUntil(() => t.IsCompleted);
        return t.GetAwaiter().GetResult();
    }

    private static void Wait(Task t)
    {
        PumpUntil(() => t.IsCompleted);
        t.GetAwaiter().GetResult();
    }

    /// <summary>창에 새 PDF(2쪽)를 열고, dirty면 한 쪽을 지워 변경 있는 상태로 만든다.</summary>
    private DocumentTabViewModel Open(MainWindow w, string name, bool dirty)
    {
        string path = _scope.File(name);
        TestPdfFactory.CreateBlank(path, 2, 595, 842);
        Wait(w.ViewModel.OpenAsync(path));
        var tab = w.ViewModel.ActiveDocument!;
        if (dirty)
        {
            var s = tab.Session!;
            Wait(s.Commands.ExecuteAsync(new DeletePagesCommand([s.Pages[1].Id])));
            Assert.True(s.IsDirty);
        }
        return tab;
    }

    [Fact]
    public void WindowX_CancelOnOneTab_KeepsWindow()
    {
        Run(host =>
        {
            var w1 = host.CreateWindow(null);
            var w2 = host.CreateWindow(w1);
            Open(w1, "clean.pdf", dirty: false);
            Open(w1, "dirty.pdf", dirty: true);
            Assert.False(Wait(w1.TryCloseAsync()));
            Assert.False(w1.IsClosed);
            Assert.Single(w1.ViewModel.Documents);   // 깨끗한 탭은 닫히고, 취소한 탭은 남는다
            Assert.False(w2.IsClosed);
        }, _prompts);
    }

    [Fact]
    public void ExitAll_StopsAtFirstCancel()
    {
        Run(host =>
        {
            var w1 = host.CreateWindow(null);
            var w2 = host.CreateWindow(w1);
            var w3 = host.CreateWindow(w2);   // 최근 순서: w3, w2, w1
            Open(w2, "dirty.pdf", dirty: true);
            Wait(host.ExitAllAsync());
            Assert.True(w3.IsClosed);
            Assert.False(w2.IsClosed);
            Assert.False(w1.IsClosed);
            Assert.False(host.IsDisposed);
        }, _prompts);
    }

    [Fact]
    public void ExitCommand_AllClean_ClosesEveryWindow()
    {
        Run(host =>
        {
            var w1 = host.CreateWindow(null);
            var w2 = host.CreateWindow(w1);
            Open(w1, "a.pdf", dirty: false);
            w2.ViewModel.Commands.Find("file.exit")!.Command.Execute(null);
            PumpUntil(() => w1.IsClosed && w2.IsClosed);
            Assert.True(host.IsDisposed);
        }, _prompts);
    }

    [Fact]
    public void LastTabClosed_InSecondWindow_ClosesWindow()
    {
        Run(host =>
        {
            var w1 = host.CreateWindow(null);
            var w2 = host.CreateWindow(w1);
            var tab = Open(w2, "a.pdf", dirty: false);
            Assert.True(Wait(w2.ViewModel.CloseDocumentAsync(tab)));
            PumpUntil(() => w2.IsClosed);
            Assert.False(w1.IsClosed);
        }, _prompts);
    }

    [Fact]
    public void LastTabClosed_InOnlyWindow_StaysEmpty()
    {
        Run(host =>
        {
            var w1 = host.CreateWindow(null);
            var tab = Open(w1, "a.pdf", dirty: false);
            Assert.True(Wait(w1.ViewModel.CloseDocumentAsync(tab)));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(w1.IsClosed);
            Assert.Empty(w1.ViewModel.Documents);
        }, _prompts);
    }

    [Fact]
    public void LastWindowClosed_DisposesHost()
    {
        Run(host =>
        {
            var w1 = host.CreateWindow(null);
            var w2 = host.CreateWindow(w1);
            w2.Close();
            PumpUntil(() => w2.IsClosed);
            Assert.False(host.IsDisposed);
            w1.Close();
            PumpUntil(() => w1.IsClosed);
            Assert.True(host.IsDisposed);
        }, _prompts);
    }
}
