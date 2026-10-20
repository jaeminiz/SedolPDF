using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Viewer;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf.IO;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Objects;

/// <summary>M4 Task 7: 헤드리스 화면에서 쪽 객체 고르기·옮기기·지우기·방향키(합성 입력 — 실제 마우스는 검증 필요).</summary>
public class ObjectInputTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-objin-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly List<DocumentSession> _sessions = [];
    private static bool _themed;

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed record Host(Window Window, DocumentView View, DocumentTabViewModel Tab, DocumentSession Session, Rect PageRect);

    private async Task<DocumentSession> OpenAsync(bool restricted = false)
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateDocumentLike(p);
        if (restricted)
        {
            using var d = PdfReader.Open(p, PdfDocumentOpenMode.Modify);
            d.SecuritySettings.OwnerPassword = "owner";
            d.SecuritySettings.PermitModifyDocument = false;
            d.Save(p);
        }
        var s = await DocumentSession.OpenAsync(_t, p, null);
        _sessions.Add(s);
        return s;
    }

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

    private Host Show(DocumentSession s) => HeadlessUi.Run(() =>
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

    private static readonly double K = ZoomMath.PointsToDip;

    /// <summary>쪽 왼쪽 위에서 (x, y)pt 떨어진 점의 화면 좌표.</summary>
    private static Point AtPt(Host h, double xPt, double yFromTopPt) => new(h.PageRect.X + xPt * K, h.PageRect.Y + yFromTopPt * K);

    private static void SetTool(Host h, ToolKind t) => HeadlessUi.Run(() => { h.Tab.Tool = t; return 0; });

    private static IReadOnlyList<ContentEdit> Edits(Host h) => h.Session.Content.For(h.Session.Pages[0].Id);

    /// <summary>객체 목록이 올 때까지 같은 자리를 누르기 전에 화면을 한 번 그려 미리 읽게 한다.</summary>
    private static void Warm(Host h) => HeadlessUi.Run(() =>
    {
        h.View.InvalidateVisual();
        var st = typeof(DocumentView).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.View)!;
        var idx = (System.Collections.IDictionary)st.GetType().GetField("ObjIndex")!.GetValue(st)!;
        Pump(() => { h.Window.UpdateLayout(); _ = h.Window.CaptureRenderedFrame(); return idx.Count > 0; });
        return 0;
    });

    private static void Drag(Host h, Point from, Point to, RawInputModifiers mods = RawInputModifiers.None) => HeadlessUi.Run(() =>
    {
        h.Window.MouseMove(from, mods);
        h.Window.MouseDown(from, MouseButton.Left, mods);
        for (int i = 1; i <= 5; i++) h.Window.MouseMove(new Point(from.X + (to.X - from.X) * i / 5, from.Y + (to.Y - from.Y) * i / 5), mods);
        h.Window.MouseUp(to, MouseButton.Left, mods);
        return 0;
    });

    private static void Key(Host h, Key k, RawInputModifiers mods = RawInputModifiers.None) =>
        HeadlessUi.Run(() => { h.View.Focus(); h.Window.KeyPress(k, mods, PhysicalKey.None, null); return 0; });

    [Fact]
    public async Task Click_SelectsImage_DragMoves_UndoRestores()
    {
        var h = Show(await OpenAsync());
        SetTool(h, ToolKind.EditObject);
        Warm(h);
        Drag(h, AtPt(h, 120, 245), AtPt(h, 160, 245));   // 이미지 가운데를 40pt 오른쪽으로
        HeadlessUi.Run(() => { Pump(() => Edits(h).Count == 1); return 0; });
        var t = Assert.IsType<TransformEdit>(Edits(h)[0]);
        Assert.Equal(40, t.Then.E, 1f);
        Assert.Equal(0, t.Then.F, 1f);
        Assert.Equal(PdfPageObjectType.Image, h.Tab.Objects.Selected!.Type);
        await h.Session.Commands.UndoAsync();
        Assert.Empty(Edits(h));
    }

    [Fact]
    public async Task Delete_Deactivates_ArrowKeysNudge_EscClears()
    {
        var h = Show(await OpenAsync());
        SetTool(h, ToolKind.EditObject);
        Warm(h);
        Drag(h, AtPt(h, 120, 245), AtPt(h, 120, 245));   // 클릭만(움직임 없음): 고르기만
        Assert.NotNull(h.Tab.Objects.Selected);
        Assert.Empty(Edits(h));
        Key(h, Avalonia.Input.Key.Right, RawInputModifiers.Shift);
        HeadlessUi.Run(() => { Pump(() => Edits(h).Count == 1); return 0; });
        Assert.Equal(10, Assert.IsType<TransformEdit>(Edits(h)[0]).Then.E, 0.01f);
        HeadlessUi.Run(() => { Pump(() => h.Tab.Objects.Selected is { } s && s.Bounds.Left > 65); return 0; });   // 새 객체 목록으로 경계를 따라감
        Key(h, Avalonia.Input.Key.Delete);
        HeadlessUi.Run(() => { Pump(() => Edits(h).Count == 2); return 0; });
        Assert.IsType<DeactivateEdit>(Edits(h)[1]);
        Assert.Null(h.Tab.Objects.Selected);
    }

    [Fact]
    public async Task EditTextTool_PicksTextOnly()
    {
        var h = Show(await OpenAsync());
        SetTool(h, ToolKind.EditText);
        Warm(h);
        Drag(h, AtPt(h, 120, 245), AtPt(h, 120, 245));   // 이미지 위
        Assert.Null(h.Tab.Objects.Selected);
        Drag(h, AtPt(h, 80, 96), AtPt(h, 80, 96));        // Arial 줄 위(기준선 100pt 바로 위)
        Assert.Equal(PdfPageObjectType.Text, h.Tab.Objects.Selected?.Type);
    }

    [Fact]
    public async Task RestrictedDoc_NoSelectionNoEdits()
    {
        var h = Show(await OpenAsync(restricted: true));
        Assert.False(h.Tab.CanEditContent);
        SetTool(h, ToolKind.EditObject);
        Drag(h, AtPt(h, 120, 245), AtPt(h, 160, 245));
        Assert.Null(h.Tab.Objects.Selected);
        Assert.Empty(Edits(h));
    }

    [Fact]
    public async Task ToolChange_ClearsObjectSelection()
    {
        var h = Show(await OpenAsync());
        SetTool(h, ToolKind.EditObject);
        Warm(h);
        Drag(h, AtPt(h, 120, 245), AtPt(h, 120, 245));
        Assert.NotNull(h.Tab.Objects.Selected);
        SetTool(h, ToolKind.Hand);
        Assert.Null(h.Tab.Objects.Selected);
    }
}
