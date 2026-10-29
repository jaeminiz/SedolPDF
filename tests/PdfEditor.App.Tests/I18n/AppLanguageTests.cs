using System.Globalization;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;

namespace PdfEditor.App.Tests.I18n;

public class AppLanguageTests
{
    [Theory]
    [InlineData(null, "ko-KR", "ko")]
    [InlineData("auto", "ko-KR", "ko")]
    [InlineData(null, "en-US", "en")]
    [InlineData(null, "ja-JP", "en")]
    [InlineData("", "de-DE", "en")]
    [InlineData("ko", "en-US", "ko")]
    [InlineData("en", "ko-KR", "en")]
    [InlineData("zz", "ko-KR", "ko")]
    public void Resolve_FollowsSettingThenInstalledLanguage(string? setting, string installed, string expected)
        => Assert.Equal(expected, AppLanguage.Resolve(setting, CultureInfo.GetCultureInfo(installed)));

    [Fact]
    public void Normalize_KeepsOnlyKnownCodes()
    {
        Assert.Equal("ko", AppLanguage.Normalize("ko"));
        Assert.Equal("en", AppLanguage.Normalize("en"));
        Assert.Null(AppLanguage.Normalize("auto"));
        Assert.Null(AppLanguage.Normalize("fr"));
        Assert.Null(AppLanguage.Normalize(null));
    }

    [Fact]
    public void Apply_SetsUiCultureForAllThreads()
    {
        var oldCur = CultureInfo.CurrentUICulture; var oldDef = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            Assert.Equal("en", AppLanguage.Apply("en"));
            Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
            string? other = null;
            var t = new Thread(() => other = CultureInfo.CurrentUICulture.Name); t.Start(); t.Join();
            Assert.Equal("en", other);
            Assert.Equal("ko", AppLanguage.Apply("ko"));
        }
        finally { CultureInfo.CurrentUICulture = oldCur; CultureInfo.DefaultThreadCurrentUICulture = oldDef; }
    }

    [Fact]
    public void FormatCulture_MatchesUiLanguage()
    {
        using (new CultureScope("en")) Assert.Equal("en", AppLanguage.FormatCulture.TwoLetterISOLanguageName);
        using (new CultureScope("ko")) Assert.Equal("ko", AppLanguage.FormatCulture.TwoLetterISOLanguageName);
    }

    [Fact]
    public void Language_IsSavedAndOldFilesLoadAsAuto()
    {
        string path = Path.Combine(Path.GetTempPath(), "lang-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(path);
            Assert.Null(store.Load().Language);
            store.Update(s => s with { Language = "en" });
            Assert.Equal("en", new SettingsStore(path).Load().Language);
            store.Update(s => s with { Language = "xx" });
            Assert.Null(new SettingsStore(path).Load().Language);
            File.WriteAllText(path, "{\"RecentFiles\":[],\"RecoveryIntervalMinutes\":5}");
            Assert.Null(new SettingsStore(path).Load().Language);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
