using System.Globalization;
using System.Text.RegularExpressions;

namespace PdfEditor.Document.Annotations.Reading;

/// <summary>
/// FreeText의 /DS(CSS 선언 문자열)에서 글자 크기·색·글꼴을 뽑는다(0.10.0, 설계서 5절).
/// Acrobat은 `font: 500 'Adobe Myungjo Std',serif 12.0pt; text-align:left; color:#FF0000` 꼴로, 다른 편집기는 `font-family`·`font-size` 개별 속성으로 쓴다.
/// 굵게·기울임은 읽지 않는다(`font-weight:500`은 굵게가 아니다). 알 수 없는 선언은 건너뛰고, 못 읽으면 null.
/// </summary>
internal static partial class DefaultStyle
{
    public static (float? Size, PdfColor? Color, string? Family) Parse(string? ds)
    {
        if (string.IsNullOrWhiteSpace(ds)) return (null, null, null);
        float? size = null;
        PdfColor? color = null;
        string? family = null;
        foreach (var decl in ds.Split(';'))
        {
            int colon = decl.IndexOf(':');
            if (colon <= 0) continue;
            string key = decl[..colon].Trim().ToLowerInvariant(), value = decl[(colon + 1)..].Trim();
            switch (key)
            {
                case "font-size":
                    if (PtSize().Match(value) is { Success: true } m1) size = ParsePt(m1);
                    break;
                case "font":
                    family = value;
                    if (PtSize().Match(value) is { Success: true } m2) size = ParsePt(m2);
                    break;
                case "font-family":
                    family = value;
                    break;
                case "color":
                    color = ParseHex(value);
                    break;
            }
        }
        return (size, color, family);
    }

    /// <summary>글꼴 글자들(CSS 글꼴 목록, /DA 글꼴 이름 …)에 명조·바탕 계열이 있으면 Serif, 아니면 Sans. `sans-serif`의 `serif`는 세지 않는다.</summary>
    public static TextFontFamily FamilyOf(params string?[] fontTexts)
    {
        foreach (var t in fontTexts)
        {
            if (string.IsNullOrEmpty(t)) continue;
            string s = t.Replace("sans-serif", "", StringComparison.OrdinalIgnoreCase);
            if (s.Contains("serif", StringComparison.OrdinalIgnoreCase)) return TextFontFamily.Serif;
            foreach (var w in SerifWords)
                if (s.Contains(w, StringComparison.OrdinalIgnoreCase)) return TextFontFamily.Serif;
        }
        return TextFontFamily.Sans;
    }

    private static readonly string[] SerifWords = ["Myungjo", "Batang", "Gungsuh", "Times", "명조", "바탕", "궁서"];

    private static float ParsePt(Match m) => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

    private static PdfColor? ParseHex(string v)
    {
        if (v.Length != 7 || v[0] != '#') return null;
        return byte.TryParse(v.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            && byte.TryParse(v.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            && byte.TryParse(v.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)
            ? new PdfColor(r, g, b) : null;
    }

    [GeneratedRegex(@"(?<![\w.])(\d+(?:\.\d+)?)\s*pt\b", RegexOptions.IgnoreCase)]
    private static partial Regex PtSize();
}
