namespace PdfEditor.Document.Annotations;

public enum LineEnding { None, Square, Circle, Diamond, OpenArrow, ClosedArrow, Butt, Slash }

public static class LineEndingExtensions
{
    /// <summary>PDF /LE 이름 (ISO 32000 표 176).</summary>
    public static string ToPdfName(this LineEnding e) => e switch
    {
        LineEnding.None => "None",
        LineEnding.Square => "Square",
        LineEnding.Circle => "Circle",
        LineEnding.Diamond => "Diamond",
        LineEnding.OpenArrow => "OpenArrow",
        LineEnding.ClosedArrow => "ClosedArrow",
        LineEnding.Butt => "Butt",
        LineEnding.Slash => "Slash",
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };

    /// <summary>앞의 '/'는 있어도 없어도 된다. 모르는 이름(ROpenArrow 등)은 null.</summary>
    public static LineEnding? FromPdfName(string? name)
    {
        if (name is null) return null;
        if (name.StartsWith('/')) name = name[1..];
        return name switch
        {
            "None" => LineEnding.None,
            "Square" => LineEnding.Square,
            "Circle" => LineEnding.Circle,
            "Diamond" => LineEnding.Diamond,
            "OpenArrow" => LineEnding.OpenArrow,
            "ClosedArrow" => LineEnding.ClosedArrow,
            "Butt" => LineEnding.Butt,
            "Slash" => LineEnding.Slash,
            _ => null,
        };
    }
}
