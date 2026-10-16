using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PdfEditor.App.Tests.Viewer.Annotations;

/// <summary>
/// 헤드리스 Avalonia(Skia 소프트웨어 렌더)를 고정 스레드 하나에서 돌린다. xUnit v3 전용인 Avalonia.Headless.XUnit는 쓰지 않는다.
/// Avalonia는 전역 상태이므로 한 프로세스에서 한 번만 설정한다.
/// </summary>
public static class HeadlessUi
{
    private sealed class HeadlessApp : Application { }

    private static readonly object Gate = new();
    private static Thread? _thread;
    private static readonly BlockingCollection<Action> Queue = new();
    private static Exception? _setupError;

    private static void EnsureThread()
    {
        lock (Gate)
        {
            if (_thread is not null) return;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                try
                {
                    AppBuilder.Configure<HeadlessApp>()
                        .UseSkia()
                        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                        .SetupWithoutStarting();
                }
                catch (Exception e) { _setupError = e; }
                ready.Set();
                foreach (var a in Queue.GetConsumingEnumerable()) a();
            }) { IsBackground = true, Name = "HeadlessUi" };
            _thread.Start();
            ready.Wait();
        }
    }

    /// <summary>고정 UI 스레드에서 실행한다. 헤드리스 설정이 안 되면 예외.</summary>
    public static T Run<T>(Func<T> f)
    {
        EnsureThread();
        if (_setupError is not null) throw new InvalidOperationException("Headless setup failed", _setupError);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private sealed class DrawControl(Action<DrawingContext> draw) : Avalonia.Controls.Control
    {
        public override void Render(DrawingContext context) => draw(context);
    }

    /// <summary>흰 바탕에 draw를 그린 w×h 비트맵의 BGRA 픽셀.</summary>
    public static byte[] RenderBgra(int w, int h, Action<DrawingContext> draw)
        => Run(() =>
        {
            using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            var c = new DrawControl(ctx =>
            {
                ctx.FillRectangle(Brushes.White, new Rect(0, 0, w, h));
                draw(ctx);
            }) { Width = w, Height = h };
            c.Measure(new Size(w, h));
            c.Arrange(new Rect(0, 0, w, h));
            rtb.Render(c);
            var buf = new byte[w * h * 4];
            unsafe
            {
                fixed (byte* p = buf) rtb.CopyPixels(new PixelRect(0, 0, w, h), (IntPtr)p, buf.Length, w * 4);
            }
            return buf;
        });
}
