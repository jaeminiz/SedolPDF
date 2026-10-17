using System.Globalization;

namespace PdfEditor.Document.Annotations;

/// <summary>첨부 파일 이름 정리. 파일에서 읽은 이름과 파일에 쓰는 이름 모두 경로·제어 문자를 없애 "다른 이름으로 저장" 때 엉뚱한 곳에 쓰이지 않게 한다.</summary>
public static class AttachmentNames
{
    public const string Fallback = "attachment";

    /// <summary>이름 길이 상한(확장자 포함). 넘으면 확장자를 남기고 앞부분을 자른다.</summary>
    public const int MaxLength = 200;

    private static readonly HashSet<string> ReservedDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Fallback;
        // 제어 문자와 서식 문자(U+200E/F, U+202A–202E, U+2066–2069 등 방향 제어)를 없앤다: 확장자를 가리는 표시 속임수를 막는다.
        string n = new([.. name.Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)]);
        n = Path.GetFileName(n.Replace('\\', '/'));
        // 경로 이동 조각과 Windows에서 쓸 수 없는 문자를 없앤다.
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c.ToString(), "");
        while (n.Contains("..")) n = n.Replace("..", ".");
        n = n.Trim(' ', '.');
        if (n.Length == 0) return Fallback;

        // Windows 장치 이름(CON, NUL, COM1 ...)은 확장자가 있어도 장치로 열린다.
        int dot = n.IndexOf('.');
        string stem = (dot < 0 ? n : n[..dot]).TrimEnd(' ');
        if (ReservedDevices.Contains(stem)) n = "_" + n;

        if (n.Length > MaxLength)
        {
            int ext = n.LastIndexOf('.');
            string extension = ext > 0 && n.Length - ext <= 32 ? n[ext..] : "";
            n = n[..(MaxLength - extension.Length)] + extension;
            // 자른 자리가 서로게이트 쌍 가운데면 앞 글자를 버린다
            int cut = MaxLength - extension.Length;
            if (cut > 0 && char.IsHighSurrogate(n[cut - 1])) n = n.Remove(cut - 1, 1);
            n = n.Trim(' ', '.');
            if (n.Length == 0) return Fallback;
        }
        return n;
    }
}
