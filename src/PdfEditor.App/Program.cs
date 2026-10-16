using Avalonia;
using PdfEditor.App.Bench;

namespace PdfEditor.App;

internal static class Program
{
    /// <summary>`--bench`로 시작했으면 설정됨 (App이 창이 뜬 뒤 벤치를 돌린다).</summary>
    internal static BenchOptions? Bench { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        Bench = BenchRunner.Parse(args);
        if (Bench is not null)
        {
            // 벤치는 사용자 설정·복구 폴더를 건드리지 않는다.
            var tmp = Path.Combine(Path.GetTempPath(), "pdfeditor-bench-" + Environment.ProcessId);
            Directory.CreateDirectory(tmp);
            Environment.SetEnvironmentVariable("PDFEDITOR_RECOVERY_DIR", Path.Combine(tmp, "recovery"));
            Environment.SetEnvironmentVariable("PDFEDITOR_SETTINGS_PATH", Path.Combine(tmp, "settings.json"));
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
