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
        if (Bench is null && Environment.GetEnvironmentVariable("PDFEDITOR_NO_SINGLE_INSTANCE") is null)
        {
            // 이미 실행 중이면 파일 경로를 넘겨 새 탭으로 열고 끝낸다. 못 넘기면(응답 없음) 평소처럼 새로 뜬다.
            Instance = SingleInstance.TryBecomePrimary(SingleInstance.DefaultName);
            if (Instance is null)
            {
                var files = args.Where(x => !x.StartsWith("-") && File.Exists(x)).Select(Path.GetFullPath).ToList();
                if (SingleInstance.TrySend(SingleInstance.DefaultName, files)) return;
            }
        }
        ApplyLanguage();
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { Instance?.Dispose(); }
    }

    /// <summary>처음 뜬 프로세스일 때만 설정됨(프로세스 수명 동안 쥐고 있어야 한다).</summary>
    internal static SingleInstance? Instance { get; private set; }

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
