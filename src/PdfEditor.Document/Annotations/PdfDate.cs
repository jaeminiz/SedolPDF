using System.Globalization;
using System.Text.RegularExpressions;

namespace PdfEditor.Document.Annotations;

/// <summary>PDF 날짜 문자열 (D:YYYYMMDDHHmmSSOHH'mm').</summary>
public static partial class PdfDate
{
    public static string Format(DateTimeOffset t)
    {
        var o = t.Offset;
        char sign = o < TimeSpan.Zero ? '-' : '+';
        var a = o.Duration();
        return string.Create(CultureInfo.InvariantCulture,
            $"D:{t.Year:D4}{t.Month:D2}{t.Day:D2}{t.Hour:D2}{t.Minute:D2}{t.Second:D2}{sign}{a.Hours:D2}'{a.Minutes:D2}'");
    }

    public static bool TryParse(string? s, out DateTimeOffset t)
    {
        t = default;
        if (string.IsNullOrEmpty(s)) return false;
        var m = DateRegex().Match(s);
        if (!m.Success) return false;

        int G(int i, int def) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : def;
        try
        {
            var local = new DateTime(G(1, 0), G(2, 1), G(3, 1), G(4, 0), G(5, 0), G(6, 0), DateTimeKind.Unspecified);
            TimeSpan off;
            if (m.Groups[7].Success) off = TimeSpan.Zero;
            else if (m.Groups[8].Success)
            {
                int hh = G(9, 0), mm = G(10, 0);
                if (hh > 14 || mm > 59) return false;
                off = new TimeSpan(hh, mm, 0);
                if (m.Groups[8].Value == "-") off = -off;
            }
            else off = TimeZoneInfo.Local.GetUtcOffset(local);
            t = new DateTimeOffset(local, off);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [GeneratedRegex("^(?:D:)?(\\d{4})(\\d{2})?(\\d{2})?(\\d{2})?(\\d{2})?(\\d{2})?(?:(Z)|([+-])(\\d{2})(?:'?(\\d{2}))?'?)?$")]
    private static partial Regex DateRegex();
}
