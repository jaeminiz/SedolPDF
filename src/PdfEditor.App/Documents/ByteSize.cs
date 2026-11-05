using System.Globalization;

namespace PdfEditor.App.Documents;

/// <summary>파일 크기 표기와 "목표 용량(MB)" 입력 해석. 1 MB = 1024 × 1024 바이트(탐색기와 같은 단위).</summary>
public static class ByteSize
{
    private const long Mb = 1024L * 1024;
    public const int MinMegabytes = 1, MaxMegabytes = 2000;

    /// <summary>예: 512 B, 2 KB, 1.5 MB, 49.9 MB. 소수 첫째 자리까지만(0이면 생략).</summary>
    public static string Format(long bytes)
    {
        if (bytes < 1024) return $"{Math.Max(0, bytes)} B";
        string[] units = ["KB", "MB", "GB"];
        double v = bytes;
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture) + " " + units[i];
    }

    /// <summary>"16", "1.5", "1,5"처럼 1~2000 사이의 숫자(MB)를 바이트로 바꾼다. 그 밖의 입력(빈 칸, 글자, 음수, 지수 표기, 범위 밖)은 false.</summary>
    public static bool TryParseMegabytes(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim().Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double mb)) return false;
        if (mb < MinMegabytes || mb > MaxMegabytes) return false;
        bytes = (long)Math.Round(mb * Mb);
        return true;
    }
}
