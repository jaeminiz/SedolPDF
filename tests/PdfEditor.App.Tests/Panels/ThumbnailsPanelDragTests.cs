using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Settings;
using PdfEditor.App.Tests.Documents;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Viewer;
using PdfEditor.Document;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

/// <summary>진짜 ThumbnailsPanel(여러 열)에서 마우스로 쪽을 끌어 순서를 바꾸는 흐름을 헤드리스로 돌려 본다.</summary>
public class ThumbnailsPanelDragTests : IAsyncLifetime
{
    private sealed class FakeImage(int w, int h) : IImageHandle
    {
        public int Width { get; } = w;
        public int Height { get; } = h;
        public void Dispose() { }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-thd-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly List<DocumentSession> _sessions = [];

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }
    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<(DocumentSession S, DocumentTabViewModel Tab, ThumbnailsViewModel Vm)> SetUp(int pages)
    {
        string path = Path.Combine(_dir, "t.pdf");
        TestPdfFactory.CreateLabeled(path, Enumerable.Range(0, pages).Select(i => (100f + 10 * i, 200f)).ToList());
        var prompts = new FakePrompts();
        var wf = new FileWorkflow(_t, new SaveService([]), new SettingsStore(Path.Combine(_dir, "s.json")), prompts);
        var s = (await wf.OpenAsync(path))!;
        _sessions.Add(s);
        var tab = new DocumentTabViewModel(s, "t", null);
        var vm = new ThumbnailsViewModel(tab, (_, w, h) => new FakeImage(w, h));
        tab.PageCommands = new PageCommandsViewModel(tab, wf, prompts);
        tab.Thumbnails = vm;
        return (s, tab, vm);
    }

    private static void Pump(Window w)
    {
        for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); }
    }

    private static Point Center(Window w, ThumbnailsPanel panel, int index, double fx)
    {
        var item = panel.FindDescendantOfType<ListBox>()!.ContainerFromIndex(index)!;
        return item.TranslatePoint(new Point(item.Bounds.Width * fx, item.Bounds.Height / 2), w)!.Value;
    }

    [Fact]
    public async Task MultiColumn_DragPageToTheRightHalfOfNext_MovesItAfter_AndShowsVerticalCaret()
    {
        var (s, _, vm) = await SetUp(8);
        var first = s.Pages[0].Id;
        var expected = new[] { s.Pages[1].Id, s.Pages[0].Id }.Concat(s.Pages.Skip(2).Select(p => p.Id)).ToList();

        var (cols, caretW, caretVisible) = HeadlessUi.Run(() =>
        {
            if (Avalonia.Application.Current is { } app && !app.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any())
                app.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            var panel = new ThumbnailsPanel { DataContext = vm };
            var w = new Window { Width = 420, Height = 520, Content = panel };
            w.Show();
            Pump(w);
            var list = panel.FindDescendantOfType<ListBox>()!;
            var grid = (ThumbnailGridPanel)list.ItemsPanelRoot!;
            var from = Center(w, panel, 0, 0.5);
            var to = Center(w, panel, 1, 0.9);   // 둘째 칸 오른쪽 → 그 뒤
            w.MouseDown(from, MouseButton.Left);
            for (int i = 1; i <= 6; i++) w.MouseMove(new Point(from.X + (to.X - from.X) * i / 6, from.Y + (to.Y - from.Y) * i / 6), RawInputModifiers.LeftMouseButton);
            Pump(w);
            var drop = panel.FindControl<Border>("DropLine")!;
            var res = (grid.Columns, drop.Width, drop.IsVisible);
            w.MouseUp(to, MouseButton.Left);
            for (int i = 0; i < 200 && !s.Pages.Select(p => p.Id).SequenceEqual(expected); i++) { Pump(w); Thread.Sleep(25); }
            w.Close();
            return res;
        });

        Assert.True(cols >= 2, $"columns {cols}");
        Assert.True(caretVisible);
        Assert.Equal(ThumbnailGridLayout.CaretThickness, caretW);   // 세로 막대(폭 3)
        for (int i = 0; i < 200 && !s.Pages.Select(p => p.Id).SequenceEqual(expected); i++) await Task.Delay(25);
        Assert.Equal(expected, s.Pages.Select(p => p.Id).ToList());
        Assert.Equal(first, s.Pages[1].Id);
    }

    [Fact]
    public async Task MultiColumn_DragWithoutMoving_JustSelects_AndDoesNotReorder()
    {
        var (s, tab, vm) = await SetUp(6);
        var before = s.Pages.Select(p => p.Id).ToList();
        HeadlessUi.Run(() =>
        {
            if (Avalonia.Application.Current is { } app && !app.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any())
                app.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            var panel = new ThumbnailsPanel { DataContext = vm };
            var w = new Window { Width = 420, Height = 520, Content = panel };
            w.Show();
            Pump(w);
            var p = Center(w, panel, 2, 0.5);
            w.MouseDown(p, MouseButton.Left);
            w.MouseUp(p, MouseButton.Left);
            Pump(w);
            w.Close();
            return 0;
        });
        await Task.Delay(200);
        Assert.Equal(before, s.Pages.Select(p => p.Id).ToList());
        Assert.Equal(2, tab.CurrentPageIndex);
    }
}
