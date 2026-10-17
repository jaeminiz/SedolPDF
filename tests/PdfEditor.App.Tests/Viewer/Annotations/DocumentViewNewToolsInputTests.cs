using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Shell;
using PdfEditor.App.Viewer;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Annotations;

/// <summary>
/// M3a Task 13: 새 도구의 화면 입력 연결. 헤드리스 Avalonia에서 DocumentView에 마우스·키 이벤트를 합성해 넣는다
/// (실제 마우스·펜·터치가 아님 — 그쪽은 검증 필요).
/// </summary>
[Collection("TabSettings")]
public class DocumentViewNewToolsInputTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-nt-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly List<DocumentSession> _sessions = [];
    private AppearanceServices? _services;

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
        Directory.CreateDirectory(_dir);
        _services = AppearanceServices.Create(FontRoot());
        DocumentTabViewModel.Appearance = _services;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        _t.Dispose();
        DocumentTabViewModel.Appearance = null;
        AppearanceGeometry.TextFont = null;
        AppearanceGeometry.ImageResolver = null;
        AppearanceGeometry.Assets = AnnotationAssets.Empty;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed record Host(Window Window, DocumentView View, DocumentTabViewModel Tab, DocumentSession Session, Rect PageRect);

    private async Task<DocumentSession> OpenAsync(Action<string> create)
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        create(p);
        var s = await DocumentSession.OpenAsync(_t, p, null);
        _sessions.Add(s);
        return s;
    }

    private static bool _themed;

    private static void Pump(Func<bool> done, int ms = 6000)
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

    private static object? Field(object o, string name) =>
        o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.GetValue(o);

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
            var w = new Window { Width = 900, Height = 1000, Content = new ScrollViewer { Content = view } };
            w.Show();
            Pump(() => { w.UpdateLayout(); return Field(view, "_layout") is not null; });
            var lay = (PageLayout)Field(view, "_layout")!;
            return new Host(w, view, tab, s, lay.GetPageRect(0));
        });
    }

    private static readonly double K = ZoomMath.PointsToDip;

    private static Point At(Host h, double x, double y) => new(h.PageRect.X + x, h.PageRect.Y + y);

    /// <summary>쪽 좌표(pt, 높이 600)를 화면(DIP) 점으로.</summary>
    private static Point AtPt(Host h, PdfPointF p, float pageHeight = 600) => At(h, p.X * K, (pageHeight - p.Y) * K);

    private static void Drag(Host h, Point from, Point to, int steps = 6, KeyModifiers mods = KeyModifiers.None)
    {
        HeadlessUi.Run(() =>
        {
            var raw = (RawInputModifiers)(int)mods;
            h.Window.MouseMove(from);
            h.Window.MouseDown(from, MouseButton.Left, raw);
            for (int i = 1; i <= steps; i++)
                h.Window.MouseMove(new Point(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps), raw);
            h.Window.MouseUp(to, MouseButton.Left, raw);
            return 0;
        });
    }

    private static void Click(Host h, Point p) => Drag(h, p, p, steps: 1);

    private static void Press(Host h, Avalonia.Input.Key key, PhysicalKey pk) => HeadlessUi.Run(() =>
    {
        h.View.Focus();
        h.Window.KeyPress(key, RawInputModifiers.None, pk, null);
        return 0;
    });

    private static IReadOnlyList<Annotation> Items(Host h) => [.. h.Session.Annotations.Get(h.Session.Pages[0].Id).Items.Select(v => v.Data)];
    private static void WaitCount(Host h, int n) => HeadlessUi.Run(() => { Pump(() => Items(h).Count == n); return 0; });
    private static void SetTool(Host h, ToolKind t) => HeadlessUi.Run(() => { h.Tab.Tool = t; return 0; });

    [Fact]
    public async Task Line_Drag_CreatesLine_ThenEndpointHandleMovesOnlyThatEnd()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.Line);
        Drag(h, At(h, 100, 200), At(h, 100 + 120, 200));
        WaitCount(h, 1);
        var line = Items(h)[0];
        Assert.Equal(AnnotKind.Line, line.Kind);
        Assert.Equal(2, line.Vertices.Length);
        Assert.Equal(PdfAnnotFlags.Print, line.Flags & PdfAnnotFlags.Print);

        // 선택 → 끝점 손잡이 끌기: 끝점만 움직이고 시작점은 그대로
        SetTool(h, ToolKind.AnnotSelect);
        Click(h, AtPt(h, new PdfPointF((line.Vertices[0].X + line.Vertices[1].X) / 2, line.Vertices[0].Y)));
        HeadlessUi.Run(() => { Pump(() => h.Tab.Selection.Ids.Count == 1); return 0; });
        var endPt = AtPt(h, line.Vertices[1]);
        Drag(h, endPt, new Point(endPt.X + 40, endPt.Y));
        HeadlessUi.Run(() => { Pump(() => Items(h).Count == 1 && Math.Abs(Items(h)[0].Vertices[1].X - line.Vertices[1].X - 40 / K) < 1.0); return 0; });
        var moved = Items(h)[0];
        Assert.Equal(line.Vertices[0], moved.Vertices[0]);
        await s.Commands.UndoAsync();
        HeadlessUi.Run(() => { Pump(() => Items(h).Count == 1 && Items(h)[0].Vertices[1] == line.Vertices[1]); return 0; });
    }

    [Fact]
    public async Task Polygon_ClicksThenEnter_CreatesPolygon_AndBackspaceDropsLastPoint()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.Polygon);
        Click(h, At(h, 100, 200));
        Click(h, At(h, 250, 200));
        Click(h, At(h, 250, 320));
        Click(h, At(h, 120, 340));
        Press(h, Avalonia.Input.Key.Back, PhysicalKey.Backspace);   // 마지막 점 취소 → 3점
        Press(h, Avalonia.Input.Key.Enter, PhysicalKey.Enter);
        WaitCount(h, 1);
        var poly = Items(h)[0];
        Assert.Equal(AnnotKind.Polygon, poly.Kind);
        Assert.Equal(3, poly.Vertices.Length);

        // 점 2개로는 다각형이 만들어지지 않는다
        Click(h, At(h, 100, 400));
        Click(h, At(h, 200, 420));
        Press(h, Avalonia.Input.Key.Enter, PhysicalKey.Enter);
        HeadlessUi.Run(() => { Thread.Sleep(200); Dispatcher.UIThread.RunJobs(); return 0; });
        Assert.Single(Items(h));
    }

    [Fact]
    public async Task PolyLine_DoubleClickFinishes_EscCancels()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.PolyLine);
        Click(h, At(h, 100, 200));
        Click(h, At(h, 220, 240));
        var last = At(h, 300, 330);
        HeadlessUi.Run(() =>
        {
            h.Window.MouseMove(last);
            for (int i = 0; i < 2; i++) { h.Window.MouseDown(last, MouseButton.Left); h.Window.MouseUp(last, MouseButton.Left); }
            return 0;
        });
        WaitCount(h, 1);
        Assert.Equal(AnnotKind.PolyLine, Items(h)[0].Kind);
        Assert.Equal(3, Items(h)[0].Vertices.Length);

        // Esc: 모은 점을 모두 버리고 아무것도 만들지 않는다
        SetTool(h, ToolKind.PolyLine);
        Click(h, At(h, 100, 400));
        Click(h, At(h, 220, 440));
        Press(h, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        HeadlessUi.Run(() => { Thread.Sleep(200); Dispatcher.UIThread.RunJobs(); return 0; });
        Assert.Single(Items(h));
    }

    private static TextBox EditorBox(Host h) => HeadlessUi.Run(() =>
    {
        Pump(() => (bool)h.View.GetType().GetProperty("IsFreeTextEditorOpen", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.View)!);
        var ed = (FreeTextEditor)h.View.GetType().GetProperty("FreeTextEditorForTest", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.View)!;
        return ed.FindControl<TextBox>("Box")!;
    });

    private static void CloseFreeText(Host h) => HeadlessUi.Run(() =>
    {
        ((Popup)Field(h.View, "_ftPopup")!).IsOpen = false;   // 바깥 클릭과 같은 닫힘 경로
        return 0;
    });

    [Fact]
    public async Task TextBox_CreateEditCommit_KoreanText_EmptyCreatesNothing()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.TextBox);
        Drag(h, At(h, 100, 300), At(h, 100 + 200, 300 + 80));
        var box = EditorBox(h);
        Assert.Empty(Items(h));   // 입력을 확정할 때 한 번에 만든다
        HeadlessUi.Run(() => { box.Text = "안녕하세요 한글 텍스트 상자"; return 0; });
        CloseFreeText(h);
        WaitCount(h, 1);
        var a = Items(h)[0];
        Assert.Equal(AnnotKind.FreeText, a.Kind);
        Assert.Equal(FreeTextIntent.TextBox, a.Intent);
        Assert.Equal("안녕하세요 한글 텍스트 상자", a.Contents);
        Assert.Equal(PdfAnnotFlags.Print, a.Flags & PdfAnnotFlags.Print);

        // 빈 내용으로 확정하면 새 주석을 만들지 않는다
        Drag(h, At(h, 100, 450), At(h, 100 + 160, 450 + 60));
        EditorBox(h);
        CloseFreeText(h);
        HeadlessUi.Run(() => { Thread.Sleep(250); Dispatcher.UIThread.RunJobs(); return 0; });
        Assert.Single(Items(h));

        // 두 번 눌러 고치기: 내용이 바뀌면 명령 하나(실행 취소로 이전 글)
        SetTool(h, ToolKind.AnnotSelect);
        HeadlessUi.Run(() =>
        {
            var p = At(h, 150, 340);
            h.Window.MouseMove(p);
            for (int i = 0; i < 2; i++) { h.Window.MouseDown(p, MouseButton.Left); h.Window.MouseUp(p, MouseButton.Left); }
            return 0;
        });
        var box2 = EditorBox(h);
        Assert.Equal("안녕하세요 한글 텍스트 상자", HeadlessUi.Run(() => box2.Text));
        HeadlessUi.Run(() => { box2.Text = "고친 글"; return 0; });
        CloseFreeText(h);
        HeadlessUi.Run(() => { Pump(() => Items(h)[0].Contents == "고친 글"); return 0; });
        await s.Commands.UndoAsync();
        HeadlessUi.Run(() => { Pump(() => Items(h)[0].Contents == "안녕하세요 한글 텍스트 상자"); return 0; });
    }

    [Fact]
    public async Task TextBox_Esc_CancelsWithoutCreating_AndCommitSavesAndReopensKorean()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.Typewriter);
        Click(h, At(h, 100, 100));
        var box = EditorBox(h);
        HeadlessUi.Run(() => { box.Text = "취소할 글"; h.Window.KeyPress(Avalonia.Input.Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); return 0; });
        HeadlessUi.Run(() => { Thread.Sleep(250); Dispatcher.UIThread.RunJobs(); return 0; });
        Assert.Empty(Items(h));

        SetTool(h, ToolKind.Typewriter);
        Click(h, At(h, 100, 100));
        var box2 = EditorBox(h);
        HeadlessUi.Run(() => { box2.Text = "타자기 한글 ABC"; return 0; });
        CloseFreeText(h);
        WaitCount(h, 1);
        var t = Items(h)[0];
        Assert.Equal(FreeTextIntent.Typewriter, t.Intent);
        Assert.True(t.Rect.Width > 20f);   // 상자가 글에 맞춰 늘어난다

        string outPath = Path.Combine(_dir, "kr.pdf");
        await new SaveService([], _services!.Appearance, _services.Patcher, _services.Assets).SaveAsync(s, outPath);
        var s2 = await DocumentSession.OpenAsync(_t, outPath, null);
        _sessions.Add(s2);
        await s2.AnnotationsReady;
        var re = s2.Annotations.Get(s2.Pages[0].Id).Items.Select(v => v.Data).ToList();
        Assert.Single(re);
        Assert.Equal(AnnotKind.FreeText, re[0].Kind);
        Assert.Equal("타자기 한글 ABC", re[0].Contents);
    }

    [Fact]
    public async Task Typewriter_Commit_WhenFitFails_RaisesNoticeInsteadOfThrowing()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        var notices = new List<string>();
        h.Tab.NoticeRequested += notices.Add;
        SetTool(h, ToolKind.Typewriter);
        Click(h, At(h, 100, 100));
        var box = EditorBox(h);
        HeadlessUi.Run(() =>
        {
            box.Text = "글꼴 없음";
            // 앱 글꼴이 없을 때 FitTypewriter가 던지는 것과 같은 예외
            h.Tab.Tools.FitTypewriter = _ => throw new InvalidOperationException("font missing");
            return 0;
        });
        CloseFreeText(h);   // 예외가 올라오면 여기서 테스트가 실패한다
        HeadlessUi.Run(() => { Pump(() => notices.Count == 1); return 0; });
        Assert.Equal(Strings.Msg_AnnotationFailed, notices[0]);
        Assert.Empty(Items(h));
    }

    [Fact]
    public async Task Stamp_Click_PlacesLastStamp_CenteredAtClick()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.Stamp);
        Click(h, At(h, 200 * K, 300 * K));
        WaitCount(h, 1);
        var st = Items(h)[0];
        Assert.Equal(AnnotKind.Stamp, st.Kind);
        Assert.Equal(StampKind.Builtin, st.Stamp!.Kind);
        Assert.Equal("Approved", st.Stamp.BuiltinName);
        Assert.Equal(Strings.Stamp_Approved, st.Stamp.Text);   // 표시 문구는 리소스(한국어)
        Assert.Equal(150f, st.Rect.Width, 1);
        Assert.Equal(50f, st.Rect.Height, 1);
        Assert.Equal(200f, (st.Rect.Left + st.Rect.Right) / 2f, 1);
    }

    [Fact]
    public async Task ReplaceText_Drag_CreatesGroup_ThenContentEditor_AndSelectingOneSelectsBoth()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.ReplaceText);
        // "PAGE 1" 글자 위를 끈다 (쪽 왼쪽 위 (26,15)pt 근처)
        Drag(h, At(h, 36, 14), At(h, 80, 14), steps: 10);
        WaitCount(h, 2);
        var items = Items(h);
        var strike = items.Single(a => a.Kind == AnnotKind.StrikeOut);
        var caret = items.Single(a => a.Kind == AnnotKind.Caret);
        Assert.Equal(strike.Id, caret.InReplyTo);
        Assert.Equal(ReplyType.Group, caret.ReplyKind);

        // 끝 쪽 삽입 표시의 내용 편집 창(스티커 편집 창 재사용)
        var popup = HeadlessUi.Run(() =>
        {
            Pump(() => Field(h.View, "_popup") is Popup { IsOpen: true });
            return (Popup)Field(h.View, "_popup")!;
        });
        HeadlessUi.Run(() =>
        {
            ((StickyNoteEditor)popup.Child!).FindControl<TextBox>("Box")!.Text = "바꿀 글";
            popup.IsOpen = false;
            Pump(() => Items(h).Single(a => a.Kind == AnnotKind.Caret).Contents == "바꿀 글");
            return 0;
        });

        // 하나를 누르면 둘 다 선택
        SetTool(h, ToolKind.AnnotSelect);
        var sc = Items(h).Single(a => a.Kind == AnnotKind.StrikeOut);
        var q = sc.Quads[0];
        Click(h, AtPt(h, new PdfPointF((q.P1.X + q.P4.X) / 2f, (q.P1.Y + q.P4.Y) / 2f), 600));
        HeadlessUi.Run(() => { Pump(() => h.Tab.Selection.Ids.Count == 2); return 0; });
    }

    [Fact]
    public async Task InsertText_Click_BetweenCharacters_OpensEditor_EmptyCreatesNothing_TextCreatesCaret()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var h = Show(s);
        SetTool(h, ToolKind.InsertText);
        Click(h, At(h, 50, 14));
        var popup = HeadlessUi.Run(() =>
        {
            Pump(() => Field(h.View, "_popup") is Popup { IsOpen: true });
            return (Popup)Field(h.View, "_popup")!;
        });
        HeadlessUi.Run(() => { popup.IsOpen = false; Thread.Sleep(250); Dispatcher.UIThread.RunJobs(); return 0; });
        Assert.Empty(Items(h));   // 빈 내용이면 만들지 않는다

        Click(h, At(h, 50, 14));
        popup = HeadlessUi.Run(() =>
        {
            Pump(() => Field(h.View, "_popup") is Popup { IsOpen: true });
            return (Popup)Field(h.View, "_popup")!;
        });
        HeadlessUi.Run(() =>
        {
            ((StickyNoteEditor)popup.Child!).FindControl<TextBox>("Box")!.Text = "끼워 넣을 글";
            popup.IsOpen = false;
            return 0;
        });
        WaitCount(h, 1);
        var c = Items(h)[0];
        Assert.Equal(AnnotKind.Caret, c.Kind);
        Assert.Equal("끼워 넣을 글", c.Contents);
        Assert.True(c.Rect.Height > 4f);   // 높이 = 그 글자 높이
    }

}
