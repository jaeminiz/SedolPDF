using System.Text.Json;

namespace PdfEditor.App.Settings;

/// <summary>설정 파일(JSON) 읽기/쓰기. 읽기는 예외를 던지지 않고, 쓰기는 임시 파일에 쓴 뒤 교체한다.</summary>
public sealed class SettingsStore(string filePath)
{
    public const int MaxRecent = 10;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    public static string DefaultPath =>
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
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath));
            if (s is null) return AppSettings.Default;
            int minutes = s.RecoveryIntervalMinutes > 0 ? s.RecoveryIntervalMinutes : AppSettings.Default.RecoveryIntervalMinutes;
            return new AppSettings(s.RecentFiles ?? [], minutes);
        }
        catch (Exception)
        {
            return AppSettings.Default;
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
