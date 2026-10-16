namespace PdfEditor.App.Settings;

/// <summary>도구별로 기억하는 주석 모양 (키 = ToolKind 이름). FillHex가 null이면 채우기 없음.</summary>
public sealed record ToolStyleSetting(string ColorHex, string? FillHex, float Opacity, float Width);

/// <summary>사용자 설정. RecentFiles는 최근 것이 앞. AuthorName이 비면 Environment.UserName을 쓴다.</summary>
public sealed record AppSettings(
    IReadOnlyList<string> RecentFiles,
    int RecoveryIntervalMinutes = 5,
    string? AuthorName = null,
    IReadOnlyDictionary<string, ToolStyleSetting>? ToolStyles = null)
{
    public static AppSettings Default { get; } = new([], 5);

    /// <summary>주석 작성자: 설정값, 없으면 Windows 사용자 이름.</summary>
    public string EffectiveAuthor => string.IsNullOrWhiteSpace(AuthorName) ? Environment.UserName : AuthorName.Trim();
}
