using System.Reflection;
using Avalonia;
using Pdf.Native;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using PdfEditor.App.Documents;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Viewer;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Appearance;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Objects;

/// <summary>M4 Task 8·9 화면 시험의 공통 틀: 앱 글꼴이 있는 AppearanceServices를 켜고, 문서 화면을 헤드리스 창에 올린다(합성 입력 — 실제 입력은 검증 필요).</summary>
public abstract class ObjectUiTestBase : IAsyncLifetime
{
    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "pdfeditor-objui-" + Guid.NewGuid().ToString("N"));
    protected readonly PdfiumThread Thread = new();
    private readonly List<DocumentSession> _sessions = [];
    protected AppearanceServices Services = null!;
    private static bool _themed;
    protected static readonly double K = ZoomMath.PointsToDip;

    protected sealed record Host(Window Window, DocumentView View, DocumentTabViewModel Tab, DocumentSession Session, Rect PageRect);

    private static string FontRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("assets/fonts/NotoSansKR-Regular.otf");
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Dir);
        Services = AppearanceServices.Create(FontRoot());
        DocumentTabViewModel.Appearance = Services;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        Thread.Dispose();
        DocumentTabViewModel.Appearance = null;
        AppearanceGeometry.TextFont = null;
        AppearanceGeometry.ImageResolver = null;
        AppearanceGeometry.Assets = AnnotationAssets.Empty;
        try { Directory.Delete(Dir, true); } catch { }
    }

    protected async Task<DocumentSession> OpenAsync(Action<string> create, string? password = null)
    {
        string p = Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".pdf");
        create(p);
        var s = await DocumentSession.OpenAsync(Thread, p, password);
        _sessions.Add(s);
        return s;
    }

    protected static void Pump(Func<bool> done, int ms = 8000)
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

    protected static void PumpUi(Func<bool> done, int ms = 8000) => HeadlessUi.Run(() => { Pump(done, ms); return 0; });

    protected Host Show(DocumentSession s) => HeadlessUi.Run(() =>
    {
        if (!_themed) { Application.Current!.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme()); _themed = true; }
        var tab = new DocumentTabViewModel(s, "a.pdf", a => Dispatcher.UIThread.Post(a));
        var view = new DocumentView { Tab = tab };
        var w = new Window { Width = 900, Height = 1100, Content = new ScrollViewer { Content = view } };
        w.Show();
        var f = typeof(DocumentView).GetField("_layout", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Pump(() => { w.UpdateLayout(); return f.GetValue(view) is not null; });
        return new Host(w, view, tab, s, ((PageLayout)f.GetValue(view)!).GetPageRect(0));
    });

    protected static Point AtPt(Host h, double xPt, double yFromTopPt) => new(h.PageRect.X + xPt * K, h.PageRect.Y + yFromTopPt * K);

    protected static void SetTool(Host h, ToolKind t) => HeadlessUi.Run(() => { h.Tab.Tool = t; return 0; });

    protected static IReadOnlyList<ContentEdit> Edits(Host h) => h.Session.Content.For(h.Session.Pages[0].Id);

    protected static void Click(Host h, Point at) => HeadlessUi.Run(() =>
    {
        h.Window.MouseMove(at);
        h.Window.MouseDown(at, MouseButton.Left);
        h.Window.MouseUp(at, MouseButton.Left);
        return 0;
    });

    protected static void Drag(Host h, Point from, Point to) => HeadlessUi.Run(() =>
    {
        h.Window.MouseMove(from);
        h.Window.MouseDown(from, MouseButton.Left);
        for (int i = 1; i <= 5; i++) h.Window.MouseMove(new Point(from.X + (to.X - from.X) * i / 5, from.Y + (to.Y - from.Y) * i / 5));
        h.Window.MouseUp(to, MouseButton.Left);
        return 0;
    });

    /// <summary>한 줄 편집 창의 입력칸(TextBox)에서 키를 누른 것처럼 보낸다.</summary>
    protected static void EditorKey(Host h, Key k) => HeadlessUi.Run(() =>
    {
        var box = h.View.LineEditorForTest!.FindControl<TextBox>("Box")!;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = k, KeyModifiers = KeyModifiers.None });
        return 0;
    });

    protected static T Ui<T>(Func<T> f) => Dispatcher.UIThread.CheckAccess() ? f() : HeadlessUi.Run(f);
    protected static bool EditorOpen(Host h) => Ui(() => h.View.IsLineEditorOpen);
    protected static string? Toast(Host h) => Ui(() => h.View.ToastForTest);

    protected static void WaitEditorOpen(Host h) => PumpUi(() => EditorOpen(h));
}
