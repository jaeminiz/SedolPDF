using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using PdfEditor.App.Dialogs;
using PdfEditor.App.Settings;
using PdfEditor.App.Tests.Viewer.Annotations;

namespace PdfEditor.App.Tests.I18n;

/// <summary>정보 창 링크·후원 링크·낱말 줄바꿈 (0.4.0 Task 9, D16).</summary>
public class AboutAndLinksTests
{
    [Fact]
    public void Sponsor_Link_HiddenWhenEmpty()
    {
        var set = AppLinks.Compute(storeBuild: false, "https://github.com/x/y", "");
        Assert.True(set.ShowRepository); Assert.True(set.ShowStar); Assert.False(set.ShowSponsor);
        Assert.False(AppLinks.Compute(false, "https://github.com/x/y", "   ").ShowSponsor);
        Assert.True(AppLinks.Compute(false, "https://github.com/x/y", "https://github.com/sponsors/x").ShowSponsor);
        // 현재 배포 값: 후원 페이지가 아직 없으면 비어 있어 숨겨진다
        Assert.Equal(!string.IsNullOrWhiteSpace(AppLinks.Sponsor), AppLinks.Compute(false, AppLinks.Repository, AppLinks.Sponsor).ShowSponsor);
    }

    [Fact]
    public void StoreBuild_HidesStarAndSponsor_KeepsRepository()
    {
        var set = AppLinks.Compute(storeBuild: true, "https://github.com/x/y", "https://github.com/sponsors/x");
        Assert.True(set.ShowRepository); Assert.False(set.ShowStar); Assert.False(set.ShowSponsor);
        Assert.True(AppLinks.IsStoreBuild(@"C:\Program Files\WindowsApps\ParkJaemin.SedolPDF_0.4.0.0_x64__abc\"));
        Assert.True(AppLinks.IsStoreBuild("c:/program files/windowsapps/foo/"));
        Assert.False(AppLinks.IsStoreBuild(@"C:\Users\a\AppData\Local\Programs\SedolPDF\"));
        Assert.False(AppLinks.IsStoreBuild(@"D:\tools\WindowsAppsX\SedolPDF\"));
    }

    [Theory]
    [InlineData("https://github.com/jaeminiz/SedolPDF", true)]
    [InlineData("http://example.com", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("C:\\Windows\\notepad.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Sponsor_Link_OpensExternalOnly(string? url, bool allowed)
    {
        var opened = new List<string>();
        bool ok = AppLinks.Open(url!, u => { opened.Add(u); return true; });
        Assert.Equal(allowed, ok);
        Assert.Equal(allowed ? [url!] : [], opened);
    }

    [Fact]
    public void AppLinks_OpenFailure_IsSwallowed()
        => Assert.False(AppLinks.Open("https://github.com/x/y", _ => throw new InvalidOperationException("no browser")));

    [Fact]
    public void About_Links_OpenExternalOnly()
    {
        // 정보 창의 링크 단추는 누르면 외부 열기 함수만 부른다(앱 안에서 네트워크 요청이나 새 창이 없다).
        var calls = new List<string>();
        var texts = HeadlessUi.Run(() =>
        {
            var b = AppLinks.CreateLink("x", "https://github.com/x/y", u => { calls.Add(u); return true; });
            b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            return calls.ToList();
        });
        Assert.Equal(["https://github.com/x/y"], texts);
        // 앱 계층에 HTTP 클라이언트 사용이 없다.
        string src = Path.Combine(RepoRoot(), "src", "PdfEditor.App");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .Where(f => File.ReadAllText(f).Contains("HttpClient")).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void About_NoPopupOrNag()
    {
        // 별·후원 문구는 정보 창(과 환경 설정의 후원 링크)에서만 쓰고, 실행 횟수를 세는 설정은 없다.
        string src = Path.Combine(RepoRoot(), "src", "PdfEditor.App");
        var users = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .Where(f => { string t = File.ReadAllText(f); return t.Contains("About_StarHint") || t.Contains("About_LinkStar") || t.Contains("About_LinkSponsor"); })
            .Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["AboutDialog.axaml.cs", "SettingsDialog.axaml.cs", "Strings.cs"], users);
        var names = typeof(AppSettings).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Launch", StringComparison.OrdinalIgnoreCase) || n.Contains("RunCount", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Rating", StringComparison.OrdinalIgnoreCase) || n.Contains("Nag", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void About_Wrap_NotMidWord(string lang)
    {
        var (words, tooTall, tooWide) = HeadlessUi.Run(() =>
        {
            using var _ = new CultureScope(lang);
            var d = new AboutDialog();
            d.Show();
            d.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var wrappers = d.GetVisualDescendants().OfType<WordWrapText>().ToList();
            var blocks = wrappers.SelectMany(w => w.Children.OfType<TextBlock>()).ToList();
            // 낱말 하나가 줄 하나 높이를 넘으면 낱말 중간에서 줄이 바뀐 것이다.
            int tall = blocks.Count(b => b.Bounds.Height > b.FontSize * 2.0);
            int wide = blocks.Count(b => b.TranslatePoint(new Point(b.Bounds.Width, 0), d) is { } p && p.X > d.Bounds.Width + 1);
            var all = wrappers.Select(w => w.Text).ToList();
            d.Close();
            return (all, tall, wide);
        });
        Assert.NotEmpty(words);
        Assert.Equal(0, tooTall);
        Assert.Equal(0, tooWide);
    }

    [Fact]
    public void WordWrapText_SplitsOnSpacesOnly()
    {
        Assert.Equal(["개인과", "기업", "모두", "무료로", "쓸", "수", "있습니다."], WordWrapText.Words("개인과 기업  모두 무료로\n쓸 수 있습니다."));
        Assert.Empty(WordWrapText.Words(null));
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PdfEditor.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("저장소 루트를 찾지 못했습니다.");
    }
}
