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
        ApplyLanguage();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>창을 만들기 전에 화면 언어를 정한다(설정이 없거나 읽지 못하면 자동). 바뀐 언어는 다시 시작한 뒤부터 적용된다.</summary>
    private static void ApplyLanguage()
    {
        string? setting = null;
        try { setting = new Settings.SettingsStore(Settings.AppPaths.Resolve(AppContext.BaseDirectory, Settings.AppPaths.CanWriteFolder).SettingsPath).Load().Language; }
        catch (Exception) { /* 읽지 못하면 자동 */ }
        Resources.AppLanguage.Apply(setting);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
