using Avalonia.Controls;

namespace PdfEditor.App.Dialogs;

/// <summary>환경 설정 결과: 자동 복구 간격(1~60분)과 작성자 이름(비면 Windows 사용자 이름을 쓴다).</summary>
public sealed record SettingsResult(int RecoveryMinutes, string? AuthorName);

/// <summary>환경 설정(자동 복구 간격, 작성자 이름). 확인하면 결과, 취소하면 null.</summary>
public sealed partial class SettingsDialog : Window
{
    public SettingsDialog() => InitializeComponent();

    public SettingsDialog(int recoveryMinutes, string? authorName) : this()
    {
        IntervalBox.Value = Math.Clamp(recoveryMinutes, 1, 60);
        AuthorBox.Text = authorName ?? "";
        AuthorBox.PlaceholderText = Environment.UserName;
        OkButton.Click += (_, _) => Close(new SettingsResult(
            Math.Clamp((int)(IntervalBox.Value ?? 5), 1, 60),
            string.IsNullOrWhiteSpace(AuthorBox.Text) ? null : AuthorBox.Text.Trim()));
        CancelButton.Click += (_, _) => Close(null);
    }
}
