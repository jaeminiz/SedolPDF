using PdfEditor.App.Settings;

namespace PdfEditor.App.Tests.Settings;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-set-" + Guid.NewGuid().ToString("N"));
    public SettingsStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void OldDataFolder_MigratedOnce()
    {
        string oldDir = Path.Combine(_dir, "PdfEditor");
        Directory.CreateDirectory(oldDir);
        string oldFile = Path.Combine(oldDir, "settings.json");
        File.WriteAllText(oldFile, "{\"RecoveryIntervalMinutes\": 9}");
        string p = SettingsStore.DefaultPathIn(_dir);
        Assert.Equal(Path.Combine(_dir, "SedolPDF", "settings.json"), p);
        Assert.Equal(9, new SettingsStore(p).Load().RecoveryIntervalMinutes);
        Assert.True(File.Exists(oldFile)); // 옛 파일은 남는다
        // 한 번만: 새 파일을 바꾼 뒤 다시 불러도 덮어쓰지 않는다
        File.WriteAllText(p, "{\"RecoveryIntervalMinutes\": 4}");
        File.WriteAllText(oldFile, "{\"RecoveryIntervalMinutes\": 3}");
        SettingsStore.DefaultPathIn(_dir);
        Assert.Equal(4, new SettingsStore(p).Load().RecoveryIntervalMinutes);
        Assert.Equal("{\"RecoveryIntervalMinutes\": 3}", File.ReadAllText(oldFile));
    }

    [Fact]
    public void OldDataFolder_CopyFailure_RetriedLater()
    {
        string oldDir = Path.Combine(_dir, "PdfEditor");
        Directory.CreateDirectory(oldDir);
        string oldFile = Path.Combine(oldDir, "settings.json");
        File.WriteAllText(oldFile, "{\"RecoveryIntervalMinutes\": 9}");
        string p;
        using (new FileStream(oldFile, FileMode.Open, FileAccess.Read, FileShare.None))
            p = SettingsStore.DefaultPathIn(_dir); // 복사 실패, 예외 없음
        Assert.False(File.Exists(p));
        SettingsStore.DefaultPathIn(_dir);
        Assert.Equal(9, new SettingsStore(p).Load().RecoveryIntervalMinutes);
    }
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
    public void AuthorAndToolStyles_RoundTrip_OldFileStillLoads()
    {
        string f = Path.Combine(_dir, "s.json");
        var store = new SettingsStore(f);
        store.Save(new AppSettings(["a.pdf"], 9, "Kim",
            new Dictionary<string, ToolStyleSetting> { ["Rectangle"] = new("#005FB8", "#FFD60A", 0.5f, 3f) }));
        var s = store.Load();
        Assert.Equal("Kim", s.AuthorName);
        Assert.Equal(new ToolStyleSetting("#005FB8", "#FFD60A", 0.5f, 3f), s.ToolStyles!["Rectangle"]);
        Assert.Equal(["a.pdf"], s.RecentFiles);
        store.AddRecent("b.pdf");   // 다른 값을 고쳐 써도 작성자·스타일이 남는다
        Assert.Equal("Kim", store.Load().AuthorName);
        Assert.Single(store.Load().ToolStyles!);

        // M1 형식 (RecentFiles, RecoveryIntervalMinutes만 있는 파일)
        File.WriteAllText(f, "{\"RecentFiles\":[\"old.pdf\"],\"RecoveryIntervalMinutes\":3}");
        var old = store.Load();
        Assert.Equal(["old.pdf"], old.RecentFiles);
        Assert.Equal(3, old.RecoveryIntervalMinutes);
        Assert.Null(old.AuthorName);
        Assert.Null(old.ToolStyles);
        Assert.Equal(Environment.UserName, old.EffectiveAuthor);
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
