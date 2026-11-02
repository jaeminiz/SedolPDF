namespace PdfEditor.Document.Annotations;

/// <summary>텍스트 상자·타자기·설명선의 글꼴 가족(0.5.0, 사용자 결정 U1: 앱에 들어 있는 고딕·명조만). 명조 파일이 없으면 고딕으로 그린다.</summary>
public enum TextFontFamily { Sans = 0, Serif = 1 }

/// <summary>
/// 글자 서식 값을 PDF 비표준 키 /SedolFont(이름)·/SedolFmt(정수 비트)로 옮기는 규칙. 서식 있는 글(/RC)은 쓰지 않는다 — 상자 전체 단위.
/// /DA의 글꼴은 그대로 /Helv이고 모양은 AP가 정한다.
/// </summary>
public static class TextFormat
{
    public const int BoldBit = 1, ItalicBit = 2, UnderlineBit = 4, StrikeBit = 8;
    private const int AllBits = BoldBit | ItalicBit | UnderlineBit | StrikeBit;

    public static bool Applies(Annotation a) => a.Kind == AnnotKind.FreeText;

    public static int ToBits(Annotation a)
        => (a.Bold ? BoldBit : 0) | (a.Italic ? ItalicBit : 0) | (a.Underline ? UnderlineBit : 0) | (a.StrikeThrough ? StrikeBit : 0);

    public static Annotation WithBits(Annotation a, int bits) => a with
    {
        Bold = (bits & BoldBit) != 0, Italic = (bits & ItalicBit) != 0, Underline = (bits & UnderlineBit) != 0, StrikeThrough = (bits & StrikeBit) != 0,
    };

    /// <summary>알 수 없는 비트는 버린다.</summary>
    public static int Sanitize(int bits) => bits & AllBits;

    public static string FamilyName(TextFontFamily f) => f == TextFontFamily.Serif ? "Serif" : "Sans";

    public static TextFontFamily FamilyFromName(string? n)
    {
        if (n is null) return TextFontFamily.Sans;
        if (n.StartsWith('/')) n = n[1..];
        return string.Equals(n, "Serif", StringComparison.OrdinalIgnoreCase) ? TextFontFamily.Serif : TextFontFamily.Sans;
    }

    /// <summary>비표준 키를 쓸 필요가 있는가(기본 서식이면 키를 만들지 않아 이전 파일과 같은 출력).</summary>
    public static bool NeedsKeys(Annotation a) => a.TextFamily != TextFontFamily.Sans || ToBits(a) != 0;
}
