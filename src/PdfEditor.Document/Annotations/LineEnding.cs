namespace PdfEditor.Document.Annotations;

public enum LineEnding { None, Square, Circle, Diamond, OpenArrow, ClosedArrow, Butt, Slash,
    /// <summary>속 빈 닫힌 화살촉(0.5.0). ClosedArrow는 선 색으로 채우고, 이것은 /IC(채움색)가 없으면 윤곽만 그린다. PDF /LE는 ClosedArrow로 쓰고 비표준 /SedolLE에 표시를 남긴다.</summary>
    HollowArrow }

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
        LineEnding.ClosedArrow or LineEnding.HollowArrow => "ClosedArrow",
        LineEnding.Butt => "Butt",
        LineEnding.Slash => "Slash",
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };

    /// <summary>표준 이름이 ClosedArrow이고 비표준 표시가 HollowArrow면 HollowArrow, 아니면 표준 값 그대로.</summary>
    public static LineEnding WithSedolMark(LineEnding standard, string? mark)
        => standard == LineEnding.ClosedArrow && mark is "HollowArrow" or "/HollowArrow" ? LineEnding.HollowArrow : standard;

    /// <summary>/SedolLE에 쓸 표시 이름: HollowArrow만 표시하고 나머지는 자리만 채우는 "None".</summary>
    public static string ToSedolMark(this LineEnding e) => e == LineEnding.HollowArrow ? "HollowArrow" : "None";

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
