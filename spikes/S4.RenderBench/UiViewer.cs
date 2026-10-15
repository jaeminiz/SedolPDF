using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Pdf.Native;

namespace S4.RenderBench;

/// <summary>S0/S4 확인용 최소 뷰어: 타일 캐시 + 스크롤 + Ctrl+휠 확대. 제품 코드가 아니다.</summary>
public static class UiViewer
{
    public static void Run(string pdfPath)
    {
        UiApp.PdfPath = pdfPath;
        AppBuilder.Configure<UiApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
    }
}

internal sealed class UiApp : Application
{
    public static string PdfPath = "";

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            d.MainWindow = new ViewerWindow(PdfPath);
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class ViewerWindow : Window
{
    private readonly PdfiumThread _t = new();
    private readonly PageView _view;
    private readonly TextBlock _status = new() { Margin = new Thickness(8), FontFamily = "Consolas" };

    public ViewerWindow(string path)
    {
        Title = "S4 타일 렌더링 확인 — Ctrl+휠: 확대/축소, 휠: 스크롤";
        Width = 1100;
        Height = 800;
        var doc = _t.InvokeAsync(() => PdfDocument.Open(_t, path)).GetAwaiter().GetResult();
        var page = _t.InvokeAsync(() => doc.LoadPage(0)).GetAwaiter().GetResult();
        var size = _t.InvokeAsync(() => page.SizePt).GetAwaiter().GetResult();

        _view = new PageView(_t, page, size.Width, size.Height);
        var scroll = new ScrollViewer
        {
            Content = _view,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _view.Attach(scroll);
        scroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _view.ZoomBy(e.Delta.Y > 0 ? 1.25 : 0.8);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var panel = new DockPanel();
        DockPanel.SetDock(_status, Dock.Top);
        panel.Children.Add(_status);
        panel.Children.Add(scroll);
        Content = panel;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => _status.Text = _view.StatusText();
        timer.Start();

        Closing += (_, _) => { _t.InvokeAsync(() => { page.Dispose(); doc.Dispose(); }).Wait(2000); };
    }
}

internal sealed class PageView : Control
{
    private const int Tile = 512;
    private readonly PdfiumThread _t;
    private readonly PdfPage _page;
    private readonly double _wPt, _hPt;
    private readonly Dictionary<(double s, int x, int y), WriteableBitmap> _cache = new();
    private readonly HashSet<(double s, int x, int y)> _pending = new();
    private ScrollViewer? _scroll;
    private double _scale = 0.5;
    private int _version;
    private double _lastMs, _maxMs;
    private long _tilesDone;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public PageView(PdfiumThread t, PdfPage page, double wPt, double hPt)
    {
        _t = t; _page = page; _wPt = wPt; _hPt = hPt;
        ClipToBounds = true;
    }

    public void Attach(ScrollViewer s)
    {
        _scroll = s;
        s.ScrollChanged += (_, _) => InvalidateVisual();
    }

    public void ZoomBy(double f)
    {
        _scale = Math.Clamp(_scale * f, 0.1, 8);
        _version++;
        _cache.Clear();
        _pending.Clear();
        InvalidateMeasure();
        InvalidateVisual();
    }

    public string StatusText() =>
        $"배율 {_scale:F2} | 페이지 {_wPt * _scale:F0}x{_hPt * _scale:F0}px | 캐시 {_cache.Count}타일 | 대기 {_pending.Count} | 마지막 {_lastMs:F0}ms 최대 {_maxMs:F0}ms | 누적 {_tilesDone}타일";

    protected override Size MeasureOverride(Size availableSize) => new(_wPt * _scale, _hPt * _scale);

    public override void Render(DrawingContext ctx)
    {
        double pw = _wPt * _scale, ph = _hPt * _scale;
        ctx.FillRectangle(Brushes.White, new Rect(0, 0, pw, ph));
        var off = _scroll?.Offset ?? default;
        var vp = _scroll?.Viewport ?? new Size(1000, 800);
        double x0 = Math.Max(0, off.X - Tile), y0 = Math.Max(0, off.Y - Tile);
        double x1 = Math.Min(pw, off.X + vp.Width + Tile), y1 = Math.Min(ph, off.Y + vp.Height + Tile);
        int tx0 = (int)(x0 / Tile), ty0 = (int)(y0 / Tile), tx1 = (int)((x1 - 1) / Tile), ty1 = (int)((y1 - 1) / Tile);

        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var key = (_scale, tx, ty);
                var dest = new Rect(tx * Tile, ty * Tile, Math.Min(Tile, pw - tx * Tile), Math.Min(Tile, ph - ty * Tile));
                if (_cache.TryGetValue(key, out var bmp))
                    ctx.DrawImage(bmp, new Rect(0, 0, dest.Width, dest.Height), dest);
                else
                {
                    ctx.FillRectangle(Brushes.Gainsboro, dest);
                    Request(key);
                }
            }
    }

    private void Request((double s, int x, int y) key)
    {
        if (!_pending.Add(key)) return;
        int version = _version;
        double scale = key.s;
        int px = key.x * Tile, py = key.y * Tile;
        int w = (int)Math.Min(Tile, _wPt * scale - px), h = (int)Math.Min(Tile, _hPt * scale - py);
        if (w <= 0 || h <= 0) { _pending.Remove(key); return; }
        _ = Task.Run(async () =>
        {
            if (version != _version) return;
            var sw = Stopwatch.StartNew();
            var bytes = await _t.InvokeAsync(() => version != _version ? null : _page.RenderTile(scale, px, py, w, h, true));
            double ms = sw.Elapsed.TotalMilliseconds;
            if (bytes is null) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _version) return;
                var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                using (var fb = bmp.Lock())
                    Marshal.Copy(bytes, 0, fb.Address, bytes.Length);
                if (_cache.Count > 300) _cache.Clear();
                _cache[key] = bmp;
                _pending.Remove(key);
                _lastMs = ms;
                _maxMs = Math.Max(_maxMs, ms);
                _tilesDone++;
                InvalidateVisual();
            });
        });
    }
}
