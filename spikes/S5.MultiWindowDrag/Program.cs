// 스파이크(버릴 코드): 0.8.0 단계 0. 제품 코드에 복사하지 말 것.
// 사용법: S5.MultiWindowDrag.exe <로그 폴더> [--auto]
//   --auto: 실제 마우스를 움직여(SendInput 계열) 세 경우를 자동으로 시험하고 결과를 로그에 쓴 뒤 끝낸다.
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace S5.MultiWindowDrag;

public static class Log
{
    private static readonly object Gate = new();
    public static string Path = "log.txt";
    public static void Write(string s)
    {
        lock (Gate) File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {s}{Environment.NewLine}");
    }
}

public sealed class SpikeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            var w1 = new SpikeWindow("A1", "A2", "A3") { Position = new PixelPoint(100, 100) };
            var w2 = new SpikeWindow("B1", "B2") { Position = new PixelPoint(760, 100) };
            w1.Show();
            w2.Show();
            desktop.MainWindow = w1;
            Log.Write($"windows shown W1 scaling={w1.RenderScaling} W2 scaling={w2.RenderScaling}");
            if (Program.Auto) new Thread(() => Program.RunAuto(desktop)) { IsBackground = true }.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public static class Program
{
    public static bool Auto;
    public static readonly List<string> Results = [];

    [STAThread]
    public static void Main(string[] args)
    {
        string dir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? ".";
        Directory.CreateDirectory(dir);
        Log.Path = System.IO.Path.Combine(dir, "log.txt");
        File.WriteAllText(Log.Path, "");
        Auto = args.Contains("--auto");
        Log.Write($"start auto={Auto}");
        AppBuilder.Configure<SpikeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, nint extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);
    private const uint LeftDown = 0x02, LeftUp = 0x04, KeyUp = 0x02;
    private const byte VkEscape = 0x1B;

    private static T Ui<T>(Func<T> f) => Dispatcher.UIThread.InvokeAsync(f).GetAwaiter().GetResult();

    private static void Drag(PixelPoint from, PixelPoint to, bool escBeforeRelease = false)
    {
        bool set = SetCursorPos(from.X, from.Y);
        Thread.Sleep(150);
        GetCursorPos(out var cur);
        nint under = WindowFromPoint(cur);
        Log.Write($"AUTO cursor set={set} now={cur.X},{cur.Y} under=0x{under:X} fg=0x{GetForegroundWindow():X} handles=[{string.Join(",", Ui(() => SpikeWindow.All.Select(w => $"{w.Label}=0x{w.TryGetPlatformHandle()?.Handle ?? 0:X}")))}]");
        mouse_event(LeftDown, 0, 0, 0, 0);
        Thread.Sleep(100);
        const int steps = 25;
        for (int i = 1; i <= steps; i++)
        {
            SetCursorPos(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps);
            Thread.Sleep(25);
        }
        Thread.Sleep(150);
        if (escBeforeRelease)
        {
            keybd_event(VkEscape, 0, 0, 0);
            keybd_event(VkEscape, 0, KeyUp, 0);
            Thread.Sleep(150);
        }
        mouse_event(LeftUp, 0, 0, 0, 0);
        Thread.Sleep(400);
    }

    public static void RunAuto(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            Thread.Sleep(1200);
            var w1 = Ui(() => SpikeWindow.All.First(w => w.Label == "W1"));
            var w2 = Ui(() => SpikeWindow.All.First(w => w.Label == "W2"));

            // 1) 끌어내기: W1의 A2를 창 아래 바깥(바탕화면)으로 → 새 창
            var a2 = Ui(() => w1.TabCenterOnScreen(1));
            var below = Ui(() => new PixelPoint(a2.X, w1.Position.Y + (int)(w1.Bounds.Height * w1.RenderScaling) + 200));
            Log.Write($"AUTO case1 tear-off from {a2} to {below}");
            Drag(a2, below);

            // 2) 다른 창으로: W2의 B1을 W1 탭 줄 오른쪽 빈 곳으로 → W1로 이동
            var b1 = Ui(() => w2.TabCenterOnScreen(0));
            var w1Strip = Ui(() => w1.StripScreenRect());
            var w1Spot = new PixelPoint(w1Strip.Right - 40, w1Strip.Y + w1Strip.Height / 2);
            Log.Write($"AUTO case2 move from {b1} to {w1Spot}");
            Drag(b1, w1Spot);

            // 3) Esc 취소: W1의 첫 탭을 바깥으로 끌다가 Esc, 그다음 놓기 → 아무 일 없어야 함
            int before = Ui(() => SpikeWindow.All.Count);
            var a1 = Ui(() => w1.TabCenterOnScreen(0));
            Log.Write($"AUTO case3 esc from {a1}");
            Drag(a1, new PixelPoint(a1.X, below.Y), escBeforeRelease: true);
            int after = Ui(() => SpikeWindow.All.Count);

            var w1Tabs = Ui(() => string.Join(",", w1.Strip.Children.OfType<Border>().Select(b => ((TextBlock)b.Child!).Text)));
            Log.Write($"AUTO done results=[{string.Join(",", Results)}] windows before/after esc={before}/{after} W1 tabs={w1Tabs}");
            bool ok1 = Results.Any(r => r.StartsWith("new-window-"));
            bool ok2 = Results.Contains("moved-to-W1");
            bool ok3 = Results.Contains("esc-cancel") && before == after;
            Log.Write($"AUTO verdict tearoff={ok1} other-window={ok2} esc={ok3}");
        }
        catch (Exception e) { Log.Write("AUTO error " + e); }
        Dispatcher.UIThread.Post(() => desktop.Shutdown());
    }
}
