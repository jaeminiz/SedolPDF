using PdfEditor.App.Settings;

namespace PdfEditor.App.Tests.Settings;

public class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pdfeditor-paths-" + Guid.NewGuid().ToString("N"));
    public AppPathsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static T WithEnv<T>(string? settings, string? recovery, Func<T> body)
    {
        string? oldS = Environment.GetEnvironmentVariable("PDFEDITOR_SETTINGS_PATH");
        string? oldR = Environment.GetEnvironmentVariable("PDFEDITOR_RECOVERY_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PDFEDITOR_SETTINGS_PATH", settings);
            Environment.SetEnvironmentVariable("PDFEDITOR_RECOVERY_DIR", recovery);
            return body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDFEDITOR_SETTINGS_PATH", oldS);
            Environment.SetEnvironmentVariable("PDFEDITOR_RECOVERY_DIR", oldR);
        }
    }

    [Fact]
    public void NoMarker_UsesAppData()
    {
        var p = WithEnv(null, null, () => AppPaths.Resolve(_root, _ => true));
        Assert.False(p.IsPortable);
        Assert.False(p.FellBackFromPortable);
        Assert.EndsWith(Path.Combine("SedolPDF", "settings.json"), p.SettingsPath);
        Assert.EndsWith(Path.Combine("SedolPDF", "Recovery"), p.RecoveryFolder);
        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
    }

    [Fact]
    public void Marker_UsesDataFolder()
    {
        File.WriteAllText(Path.Combine(_root, "portable.txt"), "");
        var p = WithEnv(null, null, () => AppPaths.Resolve(_root, AppPaths.CanWriteFolder));
        Assert.True(p.IsPortable);
        Assert.False(p.FellBackFromPortable);
        Assert.Equal(Path.Combine(_root, "data", "settings.json"), p.SettingsPath);
        Assert.Equal(Path.Combine(_root, "data", "Recovery"), p.RecoveryFolder);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "data")));   // 쓰기 검사용 임시 파일이 남지 않음
    }

    [Fact]
    public void PortableFolderNotWritable_FallsBackToAppData()
    {
        File.WriteAllText(Path.Combine(_root, "portable.txt"), "");
        var p = WithEnv(null, null, () => AppPaths.Resolve(_root, _ => false));
        Assert.False(p.IsPortable);
        Assert.True(p.FellBackFromPortable);
        Assert.EndsWith(Path.Combine("SedolPDF", "settings.json"), p.SettingsPath);
    }

    [Fact]
    public void CanWriteFolder_ReturnsFalseInsteadOfThrowing()
    {
        string file = Path.Combine(_root, "afile");
        File.WriteAllText(file, "");
        Assert.False(AppPaths.CanWriteFolder(Path.Combine(file, "sub")));   // 파일 아래에는 폴더를 만들 수 없음
    }

    [Fact]
    public void EnvironmentOverride_Wins()
    {
        File.WriteAllText(Path.Combine(_root, "portable.txt"), "");
        string s = Path.Combine(_root, "o", "s.json"), r = Path.Combine(_root, "o", "rec");
        var p = WithEnv(s, r, () => AppPaths.Resolve(_root, AppPaths.CanWriteFolder));
        Assert.Equal(s, p.SettingsPath);
        Assert.Equal(r, p.RecoveryFolder);
        Assert.False(p.IsPortable);
        Assert.False(p.FellBackFromPortable);
    }

    [Fact]
    public void KoreanPathWithSpaces_Works()
    {
        string exe = Path.Combine(_root, "한글 폴더", "PDF 에디터");
        Directory.CreateDirectory(exe);
        File.WriteAllText(Path.Combine(exe, "portable.txt"), "");
        var p = WithEnv(null, null, () => AppPaths.Resolve(exe, AppPaths.CanWriteFolder));
        Assert.True(p.IsPortable);
        Assert.Equal(Path.Combine(exe, "data", "settings.json"), p.SettingsPath);
    }
}
