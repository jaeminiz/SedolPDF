using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfEditor.App.Shell;
using PdfEditor.TestFixtures;
using Xunit;
using static PdfEditor.App.Tests.Hosting.AppHostWindowsTests;

namespace PdfEditor.App.Tests.Hosting;

/// <summary>0.8.0 단계 4: 탭을 포인터로 끌어 창 밖에 놓기·Esc 취소(헤드리스 포인터 입력).</summary>
public sealed class TabDragHeadlessTests : IDisposable
{
    private readonly HostTestScope _scope = new();

    public void Dispose() => _scope.Dispose();

    private string Pdf(string name)
    {
        string p = _scope.File(name);
        TestPdfFactory.CreateBlank(p, 1, 595, 842);
        return p;
    }

    /// <summary>두 탭이 열린 창을 띄우고 첫 탭 가운데(창 좌표)를 돌려준다.</summary>
    private static (MainWindow W, Point Tab) Setup(PdfEditor.App.Hosting.AppHost host, string a, string b)
    {
        var w = host.CreateWindow(null);
        w.Width = 1200; w.Height = 800;
        foreach (var f in new[] { a, b }) { var t = w.ViewModel.OpenAsync(f); PumpUntil(() => t.IsCompleted); }
        Point? at = null;
        PumpUntil(() =>
        {
            w.UpdateLayout();
            var item = w.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => i.DataContext == w.ViewModel.Documents[0]);
            if (item is null || item.Bounds.Width <= 0) return false;
            at = item.TranslatePoint(new Point(item.Bounds.Width / 3, item.Bounds.Height / 2), w);
            return at is not null;
        });
        return (w, at!.Value);
    }

    [Fact]
    public void Drag_FarBelow_CreatesWindow()
    {
        string a = Pdf("a.pdf"), b = Pdf("b.pdf");
        Run(host =>
        {
            var (w, tab) = Setup(host, a, b);
            var s = w.ViewModel.Documents[0].Session;
            w.MouseMove(tab);
            w.MouseDown(tab, MouseButton.Left);
            for (int i = 1; i <= 6; i++) w.MouseMove(new Point(tab.X + 5 * i, tab.Y + 60 * i), RawInputModifiers.LeftMouseButton);
            w.MouseUp(new Point(tab.X + 30, tab.Y + 360), MouseButton.Left);
            PumpUntil(() => host.Registry.Count == 2);
            var w2 = host.Registry.ByRecent.OfType<MainWindow>().First(x => x != w);
            Assert.Same(s, Assert.Single(w2.ViewModel.Documents).Session);
            Assert.Single(w.ViewModel.Documents);
        });
    }

    [Fact]
    public void Drag_Escape_DoesNothing()
    {
        string a = Pdf("a.pdf"), b = Pdf("b.pdf");
        Run(host =>
        {
            var (w, tab) = Setup(host, a, b);
            var first = w.ViewModel.Documents[0];
            w.MouseMove(tab);
            w.MouseDown(tab, MouseButton.Left);
            for (int i = 1; i <= 6; i++) w.MouseMove(new Point(tab.X + 5 * i, tab.Y + 60 * i), RawInputModifiers.LeftMouseButton);
            w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            w.MouseUp(new Point(tab.X + 30, tab.Y + 360), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, host.Registry.Count);
            Assert.Equal(2, w.ViewModel.Documents.Count);
            Assert.Same(first, w.ViewModel.Documents[0]);
        });
    }

    [Fact]
    public void Drag_SmallSideways_StillReorders()
    {
        string a = Pdf("a.pdf"), b = Pdf("b.pdf");
        Run(host =>
        {
            var (w, tab) = Setup(host, a, b);
            var first = w.ViewModel.Documents[0];
            w.MouseMove(tab);
            w.MouseDown(tab, MouseButton.Left);
            for (int i = 1; i <= 6; i++) w.MouseMove(new Point(tab.X + 80 * i, tab.Y + 2 * i), RawInputModifiers.LeftMouseButton);   // 탭 줄 안에서 오른쪽으로
            w.MouseUp(new Point(tab.X + 480, tab.Y + 12), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, host.Registry.Count);
            Assert.Same(first, w.ViewModel.Documents[1]);
        });
    }
}
