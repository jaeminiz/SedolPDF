using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Viewer;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Annotations;

/// <summary>
/// 헤드리스 Avalonia에서 DocumentView에 마우스 이벤트를 합성해 넣는 통합 확인 (실제 마우스·펜·터치가 아님 — 그쪽은 검증 필요).
/// </summary>
public class DocumentViewAnnotationInputTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-dv-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly List<DocumentSession> _sessions = [];

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed record Host(Window Window, DocumentView View, DocumentTabViewModel Tab, DocumentSession Session, Rect PageRect);

    private async Task<DocumentSession> OpenAsync(Action<string> create, string? name = null)
    {
        string p = Path.Combine(_dir, name ?? Guid.NewGuid().ToString("N") + ".pdf");
        create(p);
        var s = await DocumentSession.OpenAsync(_t, p, null);
        _sessions.Add(s);
        return s;
    }

    private static bool _themed;

    private static void Pump(Func<bool> done, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return;
            Thread.Sleep(15);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), "timed out");
    }

    private Host Show(DocumentSession s)
    {
        return HeadlessUi.Run(() =>
        {
            if (!_themed)
            {
                Application.Current!.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
                _themed = true;
            }
            var tab = new DocumentTabViewModel(s, "a.pdf", a => Dispatcher.UIThread.Post(a));
            var view = new DocumentView { Tab = tab };
            var sv = new ScrollViewer { Content = view };
            var w = new Window { Width = 900, Height = 1000, Content = sv };
            w.Show();
            Pump(() => { w.UpdateLayout(); return typeof(DocumentView).GetField("_layout", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view) is not null; });
            var lay = (PageLayout)typeof(DocumentView).GetField("_layout", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
            return new Host(w, view, tab, s, lay.GetPageRect(0));
        });
    }

    private static Point At(Host h, double x, double y) => new(h.PageRect.X + x, h.PageRect.Y + y);

    private static void Drag(Host h, Point from, Point to, int steps = 6)
    {
        HeadlessUi.Run(() =>
        {
            h.Window.MouseMove(from);
            h.Window.MouseDown(from, MouseButton.Left);
            for (int i = 1; i <= steps; i++)
                h.Window.MouseMove(new Point(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
            h.Window.MouseUp(to, MouseButton.Left);
            return 0;
        });
    }

    private static IReadOnlyList<Annotation> Items(Host h) => [.. h.Session.Annotations.Get(h.Session.Pages[0].Id).Items.Select(v => v.Data)];

    private static void WaitCount(Host h, int n) => HeadlessUi.Run(() => { Pump(() => Items(h).Count == n); return 0; });

    private static void SetTool(Host h, ToolKind t) => HeadlessUi.Run(() => { h.Tab.Tool = t; return 0; });

    [Fact]
    public async Task Rectangle_Drag_CreatesSquare_ThenSelectMoveDelete_Undo()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        double k = ZoomMath.PointsToDip;   // DIP per pt at 100%
        SetTool(h, ToolKind.Rectangle);
        Drag(h, At(h, 100, 100), At(h, 100 + 100 * k, 100 + 60 * k));
        WaitCount(h, 1);
        var sq = Items(h)[0];
        Assert.Equal(AnnotKind.Square, sq.Kind);
        Assert.Equal(100f, sq.Rect.Width, 1);
        Assert.Equal(60f, sq.Rect.Height, 1);
        Assert.Equal(PdfAnnotFlags.Print, sq.Flags & PdfAnnotFlags.Print);

        // 선택 → 이동 (왼쪽 가장자리를 잡아 오른쪽으로 40 DIP)
        SetTool(h, ToolKind.AnnotSelect);
        double leftEdgeX = 100 + 0, midY = 100 + 30 * k;
        Drag(h, At(h, leftEdgeX, midY), At(h, leftEdgeX + 40, midY));
        HeadlessUi.Run(() => { Pump(() => Items(h).Count == 1 && Math.Abs(Items(h)[0].Rect.Left - sq.Rect.Left - 40 / k) < 1.0); return 0; });
        Assert.Single(h.Tab.Selection.Ids);

        // Delete → 삭제, 실행 취소 → 복원
        HeadlessUi.Run(() => { h.View.Focus(); h.Window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null); return 0; });
        WaitCount(h, 0);
        await s.Commands.UndoAsync();
        WaitCount(h, 1);
        await s.Commands.RedoAsync();
        WaitCount(h, 0);
    }

    [Fact]
    public async Task Pencil_ThenEraser_SplitsStroke()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.Pencil);
        Drag(h, At(h, 100, 200), At(h, 300, 200), steps: 20);
        WaitCount(h, 1);
        Assert.Equal(AnnotKind.Ink, Items(h)[0].Kind);
        Assert.Single(Items(h)[0].InkStrokes);

        SetTool(h, ToolKind.Eraser);
        Drag(h, At(h, 200, 170), At(h, 200, 230), steps: 10);
        HeadlessUi.Run(() => { Pump(() => Items(h).Count == 1 && Items(h)[0].InkStrokes.Length == 2); return 0; });
        await s.Commands.UndoAsync();
        HeadlessUi.Run(() => { Pump(() => Items(h).Count == 1 && Items(h)[0].InkStrokes.Length == 1); return 0; });
    }

    [Fact]
    public async Task StickyNote_Click_CreatesNote_AndOpensEditor_ThenCommitsText()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.StickyNote);
        Drag(h, At(h, 150, 150), At(h, 150, 150), steps: 1);
        WaitCount(h, 1);
        Assert.Equal(AnnotKind.Text, Items(h)[0].Kind);
        var popup = HeadlessUi.Run(() =>
        {
            Pump(() => typeof(DocumentView).GetField("_popup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.View) is Popup { IsOpen: true });
            return (Popup)typeof(DocumentView).GetField("_popup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.View)!;
        });
        HeadlessUi.Run(() =>
        {
            var box = ((StickyNoteEditor)popup.Child!).FindControl<TextBox>("Box")!;
            box.Text = "안녕";
            popup.IsOpen = false;   // 바깥 클릭과 같은 닫힘 경로
            Pump(() => Items(h)[0].Contents == "안녕");
            return 0;
        });
    }

    [Fact]
    public async Task Escape_DuringDrag_CreatesNothing_AndSwitchesToSelect()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.Rectangle);
        HeadlessUi.Run(() =>
        {
            h.Window.MouseDown(At(h, 100, 100), MouseButton.Left);
            h.Window.MouseMove(At(h, 200, 200));
            h.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            h.Window.MouseMove(At(h, 220, 220));
            h.Window.MouseUp(At(h, 220, 220), MouseButton.Left);
            return 0;
        });
        HeadlessUi.Run(() => { Pump(() => h.Tab.Tool == ToolKind.AnnotSelect); Thread.Sleep(200); Dispatcher.UIThread.RunJobs(); return 0; });
        Assert.Empty(Items(h));
    }

    [Fact]
    public async Task RotatedPage_Rectangle_LandsWhereDrawn_AndSurvivesSaveAndReopen()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateWithCropBox(p, (50, 100, 500, 700)), "rot.pdf");
        await s.Commands.ExecuteAsync(new RotatePagesCommand([s.Pages[0].Id], 1));
        var h = Show(s);
        double k = ZoomMath.PointsToDip;
        SetTool(h, ToolKind.Rectangle);
        Drag(h, At(h, 100, 100), At(h, 100 + 120 * k, 100 + 50 * k));
        WaitCount(h, 1);
        var sq = Items(h)[0];
        // 90도 시계 방향 회전: 화면 가로 120pt = 페이지 세로 120pt, 화면 세로 50pt = 페이지 가로 50pt
        Assert.Equal(50f, sq.Rect.Width, 1);
        Assert.Equal(120f, sq.Rect.Height, 1);

        string outPath = Path.Combine(_dir, "out.pdf");
        await new SaveService([], new PdfEditor.Appearance.PdfAppearanceSource()).SaveAsync(s, outPath);
        var s2 = await DocumentSession.OpenAsync(_t, outPath, null);
        _sessions.Add(s2);
        await s2.AnnotationsReady;
        var reopened = s2.Annotations.Get(s2.Pages[0].Id).Items.Select(v => v.Data).ToList();
        Assert.Single(reopened);
        Assert.Equal(AnnotKind.Square, reopened[0].Kind);
        Assert.Equal(sq.Rect.Left, reopened[0].Rect.Left, 1);
        Assert.Equal(sq.Rect.Bottom, reopened[0].Rect.Bottom, 1);
    }

    private static object? Field(object o, string name) =>
        o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.GetValue(o);

    private static Popup EditorPopup(Host h) => (Popup)Field(h.View, "_popup")!;

    private static void DoubleClick(Host h, Point p) => HeadlessUi.Run(() =>
    {
        h.Window.MouseMove(p);
        for (int i = 0; i < 2; i++) { h.Window.MouseDown(p, MouseButton.Left); h.Window.MouseUp(p, MouseButton.Left); }
        return 0;
    });

    private static Annotation Note(DocumentSession s, float l, float b) => new()
    {
        Id = s.Annotations.NewId(), Kind = AnnotKind.Text, Rect = new PdfRectF(l, b, l + 20, b + 20),
        Contents = "", Flags = PdfAnnotFlags.Print, Color = PdfColor.FromHex("#FFD60A"),
    };

    [Fact]
    public async Task StickyEditor_SwitchingNotes_CommitsAOnlyToA_AndBOpensCleanly()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var page = s.Pages[0].Id;
        var a = Note(s, 50, 500);
        var b = Note(s, 250, 300);
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(page, a));
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(page, b));
        var h = Show(s);
        double k = ZoomMath.PointsToDip;
        SetTool(h, ToolKind.AnnotSelect);

        DoubleClick(h, At(h, 60 * k, (600 - 510) * k));
        HeadlessUi.Run(() => { Pump(() => Field(h.View, "_popup") is Popup { IsOpen: true }); return 0; });
        var popup = EditorPopup(h);
        HeadlessUi.Run(() => { ((StickyNoteEditor)popup.Child!).FindControl<TextBox>("Box")!.Text = "A-text"; return 0; });

        DoubleClick(h, At(h, 260 * k, (600 - 310) * k));
        HeadlessUi.Run(() =>
        {
            Pump(() => Items(h).First(x => x.Id == a.Id).Contents == "A-text" && (AnnotId)Field(h.View, "_editId")! == b.Id);
            Assert.True(popup.IsOpen);
            Assert.Equal("", ((StickyNoteEditor)popup.Child!).Text);
            popup.IsOpen = false;   // B는 바꾸지 않고 닫는다: A의 글이 B로 새지 않아야 한다
            Thread.Sleep(200); Dispatcher.UIThread.RunJobs();
            return 0;
        });
        Assert.Equal("A-text", Items(h).First(x => x.Id == a.Id).Contents);
        Assert.Equal("", Items(h).First(x => x.Id == b.Id).Contents ?? "");
    }

    [Fact]
    public async Task StickyEditor_ClosesWhenTabChanges_AndCommitsText()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var page = s.Pages[0].Id;
        var a = Note(s, 50, 500);
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(page, a));
        var h = Show(s);
        double k = ZoomMath.PointsToDip;
        SetTool(h, ToolKind.AnnotSelect);
        DoubleClick(h, At(h, 60 * k, (600 - 510) * k));
        HeadlessUi.Run(() => { Pump(() => Field(h.View, "_popup") is Popup { IsOpen: true }); return 0; });
        var popup = EditorPopup(h);
        HeadlessUi.Run(() =>
        {
            ((StickyNoteEditor)popup.Child!).FindControl<TextBox>("Box")!.Text = "kept";
            h.View.Tab = null;
            Assert.False(popup.IsOpen);
            Pump(() => Items(h).First(x => x.Id == a.Id).Contents == "kept");
            return 0;
        });
    }

    [Fact]
    public async Task Press_BeforeGeometryArrives_IsNotLost()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        double k = ZoomMath.PointsToDip;
        SetTool(h, ToolKind.Rectangle);
        HeadlessUi.Run(() =>
        {
            ((System.Collections.IDictionary?)Field(Field(h.View, "_state")!, "Geoms"))!.Clear();   // 쪽 정보를 없앤 채로 누른다
            h.Window.MouseMove(At(h, 100, 100));
            h.Window.MouseDown(At(h, 100, 100), MouseButton.Left);
            Pump(() => (int)Field(h.View, "_gesture")! != 0);   // 정보가 도착해 끌기가 시작될 때까지
            h.Window.MouseMove(At(h, 100 + 60 * k, 100 + 40 * k));
            h.Window.MouseUp(At(h, 100 + 60 * k, 100 + 40 * k), MouseButton.Left);
            return 0;
        });
        WaitCount(h, 1);
        Assert.Equal(60f, Items(h)[0].Rect.Width, 1);
    }

    [Fact]
    public async Task Esc_DuringTextMarkupDrag_CancelsMarkup()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.HighlightText);
        HeadlessUi.Run(() =>
        {
            h.Window.MouseDown(At(h, 60, 60), MouseButton.Left);
            h.Window.MouseMove(At(h, 300, 70));
            Assert.True((bool)Field(h.View, "_selecting")!);
            h.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False((bool)Field(h.View, "_selecting")!);
            Assert.Equal(1, (int)Field(h.View, "_markupEpoch")!);
            h.Window.MouseUp(At(h, 300, 70), MouseButton.Left);
            Thread.Sleep(300); Dispatcher.UIThread.RunJobs();
            return 0;
        });
        Assert.Empty(Items(h));
        Assert.Equal(ToolKind.AnnotSelect, h.Tab.Tool);
    }

    private static void SetField(object o, string name, object v) =>
        o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.SetValue(o, v);

    [Fact]
    public async Task ToolChangeToNonMarkup_MidMarkupDrag_CreatesNothing_AndClearsSelection()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.HighlightText);
        HeadlessUi.Run(() =>
        {
            h.Window.MouseDown(At(h, 60, 60), MouseButton.Left);
            h.Window.MouseMove(At(h, 300, 70));
            Assert.True((bool)Field(h.View, "_selecting")!);
            h.Tab.Tool = ToolKind.Rectangle;   // 마크업이 아닌 도구로 전환
            Assert.False((bool)Field(h.View, "_selecting")!);
            Assert.Equal(1, (int)Field(h.View, "_markupEpoch")!);
            Assert.Null(h.Tab.TextSelection.Range);
            h.Window.MouseUp(At(h, 300, 70), MouseButton.Left);
            Thread.Sleep(300); Dispatcher.UIThread.RunJobs();
            return 0;
        });
        Assert.Empty(Items(h));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolChangeOrEsc_DuringPostReleaseWait_CreatesNothing(bool useEsc)
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.HighlightText);
        HeadlessUi.Run(() =>
        {
            h.Window.MouseDown(At(h, 60, 60), MouseButton.Left);
            h.Window.MouseMove(At(h, 300, 70));
            SetField(h.View, "_charBusy", true);   // 글자 조회가 안 끝난 것처럼 만들어 놓은 뒤의 대기를 붙잡는다
            h.Window.MouseUp(At(h, 300, 70), MouseButton.Left);
            Assert.True((bool)Field(h.View, "_markupPending")!);
            if (useEsc) h.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            else h.Tab.Tool = ToolKind.Pencil;
            Assert.False((bool)Field(h.View, "_markupPending")!);
            Assert.Equal(1, (int)Field(h.View, "_markupEpoch")!);
            Assert.Null(h.Tab.TextSelection.Range);
            SetField(h.View, "_charBusy", false);
            Thread.Sleep(300); Dispatcher.UIThread.RunJobs();
            return 0;
        });
        Assert.Empty(Items(h));
    }
}
