using PdfEditor.App.Settings;

namespace PdfEditor.App.Tests.Settings;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-set-" + Guid.NewGuid().ToString("N"));
    public SettingsStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Settings_CorruptJson_ReturnsDefaults()
    {
        string f = Path.Combine(_dir, "s.json");
        File.WriteAllText(f, "{not json");
        var s = new SettingsStore(f).Load();
        Assert.Empty(s.RecentFiles);
        Assert.Equal(5, s.RecoveryIntervalMinutes);
    }

    [Fact]
    public void Settings_Missing_ReturnsDefaults()
    {
        var s = new SettingsStore(Path.Combine(_dir, "none", "s.json")).Load();
        Assert.Empty(s.RecentFiles);
        Assert.Equal(5, s.RecoveryIntervalMinutes);
    }

    [Fact]
    public void Settings_SaveLoad_RoundTrips_AndLeavesNoTemp()
    {
        string f = Path.Combine(_dir, "sub", "s.json");
        var store = new SettingsStore(f);
        store.Save(new AppSettings(["a.pdf", "한글.pdf"], 9));
        store.Save(new AppSettings(["b.pdf"], 7));   // 교체 경로
        var s = store.Load();
        Assert.Equal(["b.pdf"], s.RecentFiles);
        Assert.Equal(7, s.RecoveryIntervalMinutes);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(f)!));
    }

    [Fact]
    public void Settings_Recent_MaxTenNoDuplicates()
    {
        var store = new SettingsStore(Path.Combine(_dir, "s.json"));
        for (int i = 0; i < 12; i++) store.AddRecent($"f{i}.pdf");
        store.AddRecent("F5.pdf");   // 대소문자만 다른 중복
        var r = store.Load().RecentFiles;
        Assert.Equal(10, r.Count);
        Assert.Equal("F5.pdf", r[0]);
        Assert.Equal("f11.pdf", r[1]);
        Assert.Single(r, x => string.Equals(x, "f5.pdf", StringComparison.OrdinalIgnoreCase));
    }
}
