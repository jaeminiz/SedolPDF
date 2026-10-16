using System.Text.Json;

namespace PdfEditor.App.Settings;

/// <summary>설정 파일(JSON) 읽기/쓰기. 읽기는 예외를 던지지 않고, 쓰기는 임시 파일에 쓴 뒤 교체한다.</summary>
public sealed class SettingsStore(string filePath)
{
    public const int MaxRecent = 10;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    /// <summary>환경 변수 PDFEDITOR_SETTINGS_PATH가 있으면 그 파일(수동 확인·테스트용), 없으면 %APPDATA%\PdfEditor\settings.json.</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("PDFEDITOR_SETTINGS_PATH") is { Length: > 0 } over ? over :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdfEditor", "settings.json");

    public AppSettings Load()
    {
        lock (_gate) return LoadCore();
    }

    private AppSettings LoadCore()
    {
        try
        {
            if (!File.Exists(filePath)) return AppSettings.Default;
            var s = JsonSerializer.Deserialize<AppSettings>(ReadAllTextWithRetry(filePath));
            if (s is null) return AppSettings.Default;
            int minutes = s.RecoveryIntervalMinutes >= 1 ? Math.Min(s.RecoveryIntervalMinutes, 60) : AppSettings.Default.RecoveryIntervalMinutes;
            return new AppSettings(s.RecentFiles ?? [], minutes);
        }
        catch (Exception)
        {
            return AppSettings.Default;
        }
    }

    // 방금 교체한 파일을 바이러스 검사기·색인기가 잠시 잡고 있으면 읽기가 IOException으로 실패할 수 있다.
    // 예전에는 이때 기본값을 돌려줘서 AddRecent가 최근 목록을 지워 버릴 수 있었다(추정: SettingsStoreTests가 한 번 실패한 원인, 재현은 못 함).
    private static string ReadAllTextWithRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < 4 && File.Exists(path)) { Thread.Sleep(25 * (attempt + 1)); }
        }
    }

    public void Save(AppSettings s)
    {
        lock (_gate) SaveCore(s);
    }

    private void SaveCore(AppSettings s)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        Directory.CreateDirectory(dir);
        string tmp = filePath + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
            if (File.Exists(filePath)) File.Replace(tmp, filePath, null);
            else File.Move(tmp, filePath);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>맨 앞에 넣고 중복(대소문자 무시)을 없애며 최대 10개만 둔다.</summary>
    public void AddRecent(string path)
    {
        lock (_gate)
        {
            var cur = LoadCore();
            var list = new List<string> { path };
            list.AddRange(cur.RecentFiles.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
            SaveCore(cur with { RecentFiles = list.Take(MaxRecent).ToList() });
        }
    }
}
