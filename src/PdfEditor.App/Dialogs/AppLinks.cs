using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PdfEditor.App.Dialogs;

/// <summary>정보 창에 보일 링크 구성.</summary>
public sealed record AboutLinkSet(bool ShowRepository, bool ShowStar, bool ShowSponsor);

/// <summary>
/// 저장소·후원 주소와 링크 열기(0.4.0, D16). 모두 외부 브라우저로 여는 링크일 뿐이고 앱이 네트워크 요청을 보내지 않는다.
/// 팝업·실행 횟수 요청·기능 가림은 만들지 않는다. 스토어(MSIX) 설치본은 첫 심사 동안 별·후원 링크를 숨긴다
/// (docs/store/github-sponsors-guide.md 8절 — 실행 경로에 \WindowsApps\ 가 있으면 스토어 설치본).
/// </summary>
public static class AppLinks
{
    /// <summary>저장소 주소. "별 누르기"도 같은 페이지를 연다.</summary>
    public const string Repository = "https://github.com/jaeminiz/SedolPDF";

    /// <summary>후원 페이지 주소(GitHub Sponsors 등). 비어 있으면 후원 링크를 숨긴다 — 후원 페이지를 만든 뒤 여기에 한 번만 채운다.</summary>
    public const string Sponsor = "";

    /// <summary>스토어(MSIX) 설치본인지: 실행 폴더 경로에 \WindowsApps\ 가 들어 있는지.</summary>
    public static bool IsStoreBuild(string baseDirectory) =>
        baseDirectory.Replace('/', '\\').Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);

    public static AboutLinkSet Compute(bool storeBuild, string repository, string sponsor) => new(
        ShowRepository: !string.IsNullOrWhiteSpace(repository),
        ShowStar: !storeBuild && !string.IsNullOrWhiteSpace(repository),
        ShowSponsor: !storeBuild && !string.IsNullOrWhiteSpace(sponsor));

    public static AboutLinkSet Current => Compute(IsStoreBuild(AppContext.BaseDirectory), Repository, Sponsor);

    /// <summary>https 주소만 연다(다른 방식·파일 경로는 거부).</summary>
    public static bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>외부 브라우저로 연다. 열지 못하거나 허용되지 않는 주소면 false.</summary>
    public static bool Open(string url, Func<string, bool>? opener = null)
    {
        if (!IsAllowed(url)) return false;
        try { return (opener ?? ShellOpen)(url); }
        catch (Exception) { return false; }
    }

    private static bool ShellOpen(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return true;
    }

    /// <summary>밑줄 친 글자 모양의 링크 단추. 누르면 외부 브라우저로 연다(<paramref name="opener"/>는 시험용).</summary>
    public static Button CreateLink(string text, string url, Func<string, bool>? opener = null)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.Parse("#0F6CBD")), TextDecorations = TextDecorations.Underline },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 2),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Tag = url,
        };
        b.Click += (_, _) => Open(url, opener);
        return b;
    }
}
