using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Viewer;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf.IO;
using Xunit;

namespace PdfEditor.App.Tests.Viewer;

/// <summary>스냅숏 도구(홈 탭, 2026-10-20 사용자 요청): 끌어 고른 영역의 그림, 저장 안 한 주석 포함, 복사 금지 문서(합성 입력 — 실제 마우스는 검증 필요).</summary>
public class SnapshotTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-snap-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly List<DocumentSession> _sessions = [];
    private static bool _themed;
    private static readonly double K = ZoomMath.PointsToDip;

    private (int W, int H, byte[]? Png)? _clip;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        WinClipboard.TestSink = (_, w, h, png) => { _clip = (w, h, png); return true; };   // 사용자의 실제 클립보드를 건드리지 않는다
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        _t.Dispose();
        WinClipboard.TestSink = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void BuildDib_Is24BitOpaqueBottomUp_PaddedRows()
    {
        // 위 줄: 빨강(알파 255), 파랑(알파 255), 아래 줄: 완전 투명 두 픽셀 → 흰색으로 합성
        byte[] bgra = [0, 0, 255, 255, 255, 0, 0, 255,   0, 0, 0, 0, 0, 0, 0, 0];
        var dib = WinClipboard.BuildDib(bgra, 2, 2);
        Assert.Equal(40, BitConverter.ToInt32(dib, 0));
        Assert.Equal(2, BitConverter.ToInt32(dib, 4));
        Assert.Equal(2, BitConverter.ToInt32(dib, 8));
        Assert.Equal(24, BitConverter.ToInt16(dib, 14));
        Assert.Equal(40 + 8 * 2, dib.Length);   // 줄 6바이트 → 8바이트로 맞춤
        // 아래에서 위로: 첫 줄은 원본의 아래 줄(투명 → 흰색), 다음 줄이 원본의 위 줄
        Assert.Equal([255, 255, 255], dib[40..43]);
        Assert.Equal([0, 0, 255], dib[48..51]);        // 빨강(B,G,R = 0,0,255)
        Assert.Equal([255, 0, 0], dib[51..54]);        // 파랑
    }

    [Fact]
    public async Task Click_PutsWholePageOnClipboard_AsOpaqueDibAndPng()
    {
        var h = Show(await OpenAsync());
        Drag(h, AtPt(h, 100, 100), AtPt(h, 100, 100));
        HeadlessUi.Run(() => { Pump(() => _clip is not null); return 0; });
        Assert.InRange(_clip!.Value.W, (int)(595 * SnapshotRenderer.DefaultPixelsPerPt) - 2, (int)(595 * SnapshotRenderer.DefaultPixelsPerPt) + 2);
        Assert.NotNull(_clip.Value.Png);
        Assert.Equal(0x89, _clip.Value.Png![0]);   // PNG 서명
    }

    private async Task<DocumentSession> OpenAsync(bool noCopy = false)
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateDocumentLike(p);
        if (noCopy)
        {
            using var d = PdfReader.Open(p, PdfDocumentOpenMode.Modify);
            d.SecuritySettings.OwnerPassword = "owner";
            d.SecuritySettings.PermitExtractContent = false;
            d.Save(p);
        }
        var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.AnnotationsReady;
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

    private static async Task AddRedSquare(DocumentSession s)
    {
        var id = s.Pages[0].Id;
        var a = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(300, 600, 400, 700),
            Color = new PdfColor(220, 0, 0), FillColor = new PdfColor(220, 0, 0), BorderWidth = 1, Flags = PdfAnnotFlags.Print,
        };
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, a));
    }

    [Fact]
    public async Task Renderer_IncludesUnsavedAnnotation_AtDoubleResolution()
    {
        var s = await OpenAsync();
        await AddRedSquare(s);   // 저장하지 않은 새 주석(PDFium 문서에는 아직 없다)
        var (size, center) = HeadlessUi.Run(() =>
        {
            var task = SnapshotRenderer.RenderAsync(s, s.Pages[0].Id, new PdfRectF(280, 580, 420, 720));
            Pump(() => task.IsCompleted);
            using var bmp = task.Result!;
            var px = new byte[bmp.PixelSize.Width * bmp.PixelSize.Height * 4];
            var h = GCHandle.Alloc(px, GCHandleType.Pinned);
            try { bmp.CopyPixels(new Avalonia.PixelRect(bmp.PixelSize), h.AddrOfPinnedObject(), px.Length, bmp.PixelSize.Width * 4); }
            finally { h.Free(); }
            int i = (bmp.PixelSize.Height / 2 * bmp.PixelSize.Width + bmp.PixelSize.Width / 2) * 4;
            return (bmp.PixelSize, (B: px[i], G: px[i + 1], R: px[i + 2]));
        });
        Assert.Equal(140 * SnapshotRenderer.DefaultPixelsPerPt, size.Width, 2.0);
        Assert.Equal(140 * SnapshotRenderer.DefaultPixelsPerPt, size.Height, 2.0);
        Assert.True(center.R > 180 && center.G < 60 && center.B < 60, $"red square in the middle: {center}");
    }

    [Fact]
    public async Task Renderer_Result_IsFullyOpaque_ForAlphaAwarePasteTargets()
    {
        // 카카오톡 등 알파를 따르는 붙여넣기 대상에서 검정으로 나오지 않게 모든 픽셀의 알파가 255여야 한다(2026-10-20 사용자 보고)
        var s = await OpenAsync();
        var minAlpha = HeadlessUi.Run(() =>
        {
            var task = SnapshotRenderer.RenderAsync(s, s.Pages[0].Id, new PdfRectF(0, 0, 595, 842));
            Pump(() => task.IsCompleted);
            using var bmp = task.Result!;
            var px = new byte[bmp.PixelSize.Width * bmp.PixelSize.Height * 4];
            var h = GCHandle.Alloc(px, GCHandleType.Pinned);
            try { bmp.CopyPixels(new Avalonia.PixelRect(bmp.PixelSize), h.AddrOfPinnedObject(), px.Length, bmp.PixelSize.Width * 4); }
            finally { h.Free(); }
            int min = 255;
            for (int i = 3; i < px.Length; i += 4) min = Math.Min(min, px[i]);
            return min;
        });
        Assert.Equal(255, minAlpha);
    }

    private sealed record Host(Window Window, DocumentView View, DocumentTabViewModel Tab, Rect PageRect);

    private Host Show(DocumentSession s) => HeadlessUi.Run(() =>
    {
        if (!_themed) { Application.Current!.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme()); _themed = true; }
        var tab = new DocumentTabViewModel(s, "a.pdf", a => Dispatcher.UIThread.Post(a));
        var view = new DocumentView { Tab = tab };
        var w = new Window { Width = 900, Height = 1100, Content = new ScrollViewer { Content = view } };
        w.Show();
        var f = typeof(DocumentView).GetField("_layout", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Pump(() => { w.UpdateLayout(); return f.GetValue(view) is not null; });
        tab.Tool = ToolKind.Snapshot;
        Pump(() => { w.UpdateLayout(); _ = w.CaptureRenderedFrame(); return true; });
        return new Host(w, view, tab, ((PageLayout)f.GetValue(view)!).GetPageRect(0));
    });

    private static void Drag(Host h, Point from, Point to) => HeadlessUi.Run(() =>
    {
        // 쪽 정보가 먼저 읽히도록 화면을 몇 번 그린다
        Pump(() => { h.Window.UpdateLayout(); _ = h.Window.CaptureRenderedFrame(); return true; }, 300);
        Thread.Sleep(200);
        Dispatcher.UIThread.RunJobs();
        h.Window.MouseMove(from);
        h.Window.MouseDown(from, MouseButton.Left);
        for (int i = 1; i <= 5; i++) h.Window.MouseMove(new Point(from.X + (to.X - from.X) * i / 5, from.Y + (to.Y - from.Y) * i / 5));
        h.Window.MouseUp(to, MouseButton.Left);
        return 0;
    });

    private static Point AtPt(Host h, double xPt, double yFromTopPt) => new(h.PageRect.X + xPt * K, h.PageRect.Y + yFromTopPt * K);

    [Fact]
    public async Task Drag_MakesSnapshotOfRegion()
    {
        var h = Show(await OpenAsync());
        Drag(h, AtPt(h, 100, 100), AtPt(h, 200, 150));   // 100 × 50pt
        HeadlessUi.Run(() => { Pump(() => h.View.LastSnapshotSize is not null); return 0; });
        var size = h.View.LastSnapshotSize!.Value;
        Assert.Equal(100 * SnapshotRenderer.DefaultPixelsPerPt, size.Width, 3.0);
        Assert.Equal(50 * SnapshotRenderer.DefaultPixelsPerPt, size.Height, 3.0);
    }

    [Fact]
    public async Task Click_WithoutDrag_MakesSnapshotOfWholePage()
    {
        var h = Show(await OpenAsync());
        Drag(h, AtPt(h, 100, 100), AtPt(h, 100, 100));   // 끌지 않고 클릭만
        HeadlessUi.Run(() => { Pump(() => h.View.LastSnapshotSize is not null); return 0; });
        var size = h.View.LastSnapshotSize!.Value;
        Assert.Equal(595 * SnapshotRenderer.DefaultPixelsPerPt, size.Width, 3.0);
        Assert.Equal(842 * SnapshotRenderer.DefaultPixelsPerPt, size.Height, 3.0);
    }

    [Fact]
    public async Task CopyRestricted_NoSnapshot_NoticeShown()
    {
        var h = Show(await OpenAsync(noCopy: true));
        string? notice = null;
        h.Tab.NoticeRequested += m => notice = m;
        Drag(h, AtPt(h, 100, 100), AtPt(h, 200, 150));
        HeadlessUi.Run(() => { Pump(() => notice is not null); return 0; });
        Assert.Equal(PdfEditor.App.Resources.Strings.Msg_CopyRestricted, notice);
        Assert.Null(h.View.LastSnapshotSize);
    }
}
