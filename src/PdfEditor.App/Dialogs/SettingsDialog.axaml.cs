using Avalonia.Controls;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Dialogs;

/// <summary>환경 설정 결과: 자동 복구 간격(1~60분), 작성자 이름(비면 Windows 사용자 이름을 쓴다), 화면 언어("ko"·"en", null = 자동).</summary>
public sealed record SettingsResult(int RecoveryMinutes, string? AuthorName, string? Language = null);

/// <summary>환경 설정(자동 복구 간격, 작성자 이름). 확인하면 결과, 취소하면 null.</summary>
public sealed partial class SettingsDialog : Window
{
    public SettingsDialog() => InitializeComponent();

    public SettingsDialog(int recoveryMinutes, string? authorName, string? language = null) : this()
    {
        IntervalBox.Value = Math.Clamp(recoveryMinutes, 1, 60);
        AuthorBox.Text = authorName ?? "";
        AuthorBox.PlaceholderText = Environment.UserName;
        string?[] codes = [null, AppLanguage.Korean, AppLanguage.English];
        LanguageBox.ItemsSource = new[] { Strings.Dlg_SettingsLanguageAuto, Strings.Dlg_SettingsLanguageKo, Strings.Dlg_SettingsLanguageEn };
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(codes, AppLanguage.Normalize(language)));
        var links = AppLinks.Current;
        if (links.ShowSponsor) SponsorRow.Children.Add(AppLinks.CreateLink(Strings.About_LinkSponsor, AppLinks.Sponsor));
        SponsorRow.IsVisible = links.ShowSponsor;
        OkButton.Click += (_, _) => Close(new SettingsResult(
            Math.Clamp((int)(IntervalBox.Value ?? 5), 1, 60),
            string.IsNullOrWhiteSpace(AuthorBox.Text) ? null : AuthorBox.Text.Trim(),
            codes[Math.Clamp(LanguageBox.SelectedIndex, 0, 2)]));
        CancelButton.Click += (_, _) => Close(null);
    }
}
