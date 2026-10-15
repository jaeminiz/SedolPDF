namespace PdfEditor.App.Settings;

/// <summary>사용자 설정. RecentFiles는 최근 것이 앞.</summary>
public sealed record AppSettings(IReadOnlyList<string> RecentFiles, int RecoveryIntervalMinutes = 5)
{
    public static AppSettings Default { get; } = new([], 5);
}
