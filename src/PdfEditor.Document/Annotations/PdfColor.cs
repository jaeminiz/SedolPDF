using System.Globalization;

namespace PdfEditor.Document.Annotations;

public readonly record struct PdfColor(byte R, byte G, byte B)
{
    /// <summary>"#RRGGBB" 형식만 받는다.</summary>
    public static PdfColor FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != 7 || hex[0] != '#')
            throw new FormatException("Color must be in #RRGGBB format.");
        return new PdfColor(
            byte.Parse(hex.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}
