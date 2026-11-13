using System.Globalization;
using System.Text.RegularExpressions;

namespace PdfEditor.Document.Annotations.Reading;

/// <summary>
/// FreeText의 /DS(CSS 선언 문자열)에서 글자 크기·색·글꼴을 뽑는다(0.10.0, 설계서 5절).
/// Acrobat은 `font: 500 'Adobe Myungjo Std',serif 12.0pt; text-align:left; color:#FF0000` 꼴로, 다른 편집기는 `font-family`·`font-size` 개별 속성으로 쓴다.
/// 굵게는 `font-weight`가 bold·bolder·600 이상일 때만(0.12.0; `font-weight:500`·`font: 500 …`은 굵게가 아니다), 기울임은 italic·oblique. 알 수 없는 선언은 건너뛰고, 못 읽으면 null.
/// </summary>
internal static partial class DefaultStyle
{
    /// <summary>한 CSS 선언 문자열에서 읽은 값. 선언이 없으면 null(굵게·기울임은 `normal`이 와도 false가 아니라 "알려 줌"이므로 bool?).</summary>
    internal sealed record Css(float? Size, PdfColor? Color, string? Family, bool? Bold, bool? Italic);

    public static (float? Size, PdfColor? Color, string? Family) Parse(string? ds)
    {
        var c = ParseCss(ds);
        return (c.Size, c.Color, c.Family);
    }

    public static Css ParseCss(string? ds)
    {
        if (string.IsNullOrWhiteSpace(ds)) return new Css(null, null, null, null, null);
        float? size = null;
        PdfColor? color = null;
        string? family = null;
        bool? bold = null, italic = null;
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
                    var m2 = PtSize().Match(value);
                    if (m2.Success) size = ParsePt(m2);
                    // 약식: 크기 앞의 낱말에서 굵기·기울임을 찾는다(`font: bold italic 12pt Arial`). 이름 안의 글자와 헷갈리지 않게 크기까지만 본다.
                    string head = m2.Success ? value[..m2.Index] : value;
                    if (WeightWord().Match(head) is { Success: true } w) bold = IsBoldWeight(w.Value);
                    if (head.Contains("italic", StringComparison.OrdinalIgnoreCase) || head.Contains("oblique", StringComparison.OrdinalIgnoreCase)) italic = true;
                    break;
                case "font-family":
                    family = value;
                    break;
                case "font-weight":
                    bold = IsBoldWeight(value);
                    break;
                case "font-style":
                    italic = value.Contains("italic", StringComparison.OrdinalIgnoreCase) || value.Contains("oblique", StringComparison.OrdinalIgnoreCase);
                    break;
                case "color":
                    color = ParseHex(value);
                    break;
            }
        }
        return new Css(size, color, family, bold, italic);
    }

    private static bool IsBoldWeight(string v)
    {
        v = v.Trim();
        if (v.Equals("bold", StringComparison.OrdinalIgnoreCase) || v.Equals("bolder", StringComparison.OrdinalIgnoreCase)) return true;
        return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 600;
    }

    /// <summary>
    /// CSS 글꼴 목록(또는 `font` 약식)의 첫 번째 실제 글꼴 이름. 따옴표를 벗기고 serif·sans-serif 같은 일반 이름과 크기·굵기 낱말은 뺀다. 없으면 null.
    /// PDF에 다시 써도 안전하지 않은 글자(`' " ; &lt; &gt; &amp; \`)가 든 이름이나 64자를 넘는 이름도 null.
    /// </summary>
    public static string? FirstFontName(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return null;
        string s = PtSize().Replace(css, " ");
        s = ShorthandWords().Replace(s, " ");
        foreach (var part in s.Split(','))
        {
            string n = part.Trim().Trim('\'', '"').Trim();
            if (n.Length == 0 || Generic.Contains(n)) continue;
            return n.Length <= 64 && n.IndexOfAny(['\'', '"', ';', '<', '>', '&', '\\']) < 0 ? n : null;
        }
        return null;
    }

    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
        { "serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui" };

    [GeneratedRegex(@"(?<![\w-])(bold|bolder|lighter|normal|italic|oblique|[1-9]00)(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex ShorthandWords();

    [GeneratedRegex(@"(?<![\w-])(bold|bolder|lighter|normal|[1-9]00)(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex WeightWord();

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
