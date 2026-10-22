using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Dialogs;

/// <summary>색 고르기 대화상자의 색 계산(HSV ↔ RGB, 순수 계산). 색상 h는 0~360도, 채도 s·명도 v는 0~1.</summary>
public static class ColorMath
{
    public static PdfColor FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x),
        };
        return new PdfColor(To8(r + m), To8(g + m), To8(b + m));
    }

    public static (double H, double S, double V) ToHsv(PdfColor c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    /// <summary>"#RRGGBB" 또는 "RRGGBB"(앞뒤 공백 허용). 틀린 글이면 null.</summary>
    public static PdfColor? TryParseHex(string? text)
    {
        if (text is null) return null;
        string t = text.Trim();
        if (!t.StartsWith('#')) t = "#" + t;
        try { return PdfColor.FromHex(t); }
        catch (FormatException) { return null; }
    }

    private static byte To8(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
}
