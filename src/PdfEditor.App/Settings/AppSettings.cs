namespace PdfEditor.App.Settings;

/// <summary>도구별로 기억하는 주석 모양 (키 = ToolKind 이름). FillHex가 null이면 채우기 없음.</summary>
public sealed record ToolStyleSetting(string ColorHex, string? FillHex, float Opacity, float Width);

/// <summary>
/// 이름 붙인 주석 스타일(M3b B4). AppliesTo = AnnotKind 이름(null이면 모든 종류). FillHex·FontSizePt·StartEnding·EndEnding·CloudIntensity가
/// null이면 그 속성은 건드리지 않는다. 선 끝은 LineEnding 이름, CloudIntensity 0 = 클라우드 끄기. LineStyle(0.4.0)은 LineStyle 이름이며 옛 설정 파일에는 없어 null(= 건드리지 않음)이다.
/// </summary>
public sealed record AnnotationStyle(
    string Name, string? AppliesTo, string ColorHex, string? FillHex, float Opacity, float Width,
    float? FontSizePt = null, string? StartEnding = null, string? EndEnding = null, float? CloudIntensity = null,
    string? LineStyle = null);

/// <summary>
/// 사용자 설정. RecentFiles는 최근 것이 앞. AuthorName이 비면 Environment.UserName을 쓴다.
/// Styles가 null이면 기본 스타일 3개를 보인다 (한 번이라도 저장·삭제·이름 바꾸면 전체 목록이 저장된다. 빈 목록 = 모두 지움).
/// </summary>
public sealed record AppSettings(
    IReadOnlyList<string> RecentFiles,
    int RecoveryIntervalMinutes = 5,
    string? AuthorName = null,
    IReadOnlyDictionary<string, ToolStyleSetting>? ToolStyles = null,
    IReadOnlyList<AnnotationStyle>? Styles = null,
    bool? LeftPanelVisible = null,
    string? LeftPanelKind = null,
    bool? PropertiesPanelVisible = null)
{
    public static AppSettings Default { get; } = new([], 5);

    /// <summary>주석 작성자: 설정값, 없으면 Windows 사용자 이름.</summary>
    public string EffectiveAuthor => string.IsNullOrWhiteSpace(AuthorName) ? Environment.UserName : AuthorName.Trim();
}
