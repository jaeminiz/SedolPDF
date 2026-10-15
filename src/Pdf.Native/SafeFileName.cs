namespace Pdf.Native;

/// <summary>첨부 파일 이름을 안전한 로컬 파일 이름으로 바꾼다 (경로 탈출·예약 이름 방지).</summary>
public static class SafeFileName
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string From(string attachmentName, string fallback = "attachment")
    {
        string s = attachmentName ?? string.Empty;
        // 슬래시·역슬래시 모두 경로 구분자로 본다 (Path.GetFileName은 OS 규칙만 따르므로 직접 처리)
        int cut = s.LastIndexOfAny(['\\', '/']);
        if (cut >= 0) s = s[(cut + 1)..];

        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] == ':' || Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        s = new string(chars).Trim().Trim('.').Trim();

        if (s.Length == 0) s = fallback;
        // CON.txt 처럼 확장자가 붙은 경우도 예약 이름이다
        int dot = s.IndexOf('.');
        string stem = dot >= 0 ? s[..dot] : s;
        if (Reserved.Contains(stem.TrimEnd())) s = "_" + s;
        return s;
    }
}
