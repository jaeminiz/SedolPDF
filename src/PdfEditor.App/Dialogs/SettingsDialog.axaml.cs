using Avalonia.Controls;

namespace PdfEditor.App.Dialogs;

/// <summary>환경 설정(M1: 자동 복구 간격만). 확인하면 1~60 사이의 분, 취소하면 null.</summary>
public sealed partial class SettingsDialog : Window
{
    public SettingsDialog() => InitializeComponent();

    public SettingsDialog(int recoveryMinutes) : this()
    {
        IntervalBox.Value = Math.Clamp(recoveryMinutes, 1, 60);
        OkButton.Click += (_, _) => Close((int?)Math.Clamp((int)(IntervalBox.Value ?? 5), 1, 60));
        CancelButton.Click += (_, _) => Close(null);
    }
}
