using Pdf.Native;

namespace PdfEditor.Document.Annotations;

/// <summary>
/// FreeText·Stamp의 <see cref="Annotation.Rotation"/> 계산. Painter는 주석을 "바로 선" 자기 좌표(ToLocal)에서 그린 뒤
/// <see cref="ToPage"/> 행렬 하나로 쪽에 돌려 놓는다(화면과 AP가 같은 행렬). 회전은 90도 단위, 반시계 방향.
/// </summary>
public static class AnnotationRotation
{
    /// <summary>각도(도)를 0~3 사분면 수로. 90의 배수가 아니면 가장 가까운 배수로 반올림한다.</summary>
    public static int Quarter(int degrees) => (((int)Math.Round(degrees / 90.0) % 4) + 4) % 4;

    /// <summary>쪽 회전(0~3, PDFium)으로 만든 주석의 Rotation 값(도). 쪽이 시계 방향으로 돈 만큼 주석은 반시계로 돌려 바로 세운다.</summary>
    public static int ForPage(int pageRotation) => ((pageRotation % 4) + 4) % 4 * 90;

    private static (float Cos, float Sin) CosSin(int q) => (q % 4) switch { 0 => (1f, 0f), 1 => (0f, 1f), 2 => (-1f, 0f), _ => (0f, -1f) };

    /// <summary>c를 중심으로 q × 90도 반시계로 돌리는 행렬.</summary>
    public static PdfMatrix About(PdfPointF c, int q)
    {
        var (cos, sin) = CosSin(((q % 4) + 4) % 4);
        return new PdfMatrix(cos, sin, -sin, cos, c.X - cos * c.X + sin * c.Y, c.Y - sin * c.X - cos * c.Y);
    }

    public static PdfRectF Apply(PdfMatrix m, PdfRectF r)
        => PdfRectF.FromPoints([m.Apply(new(r.Left, r.Bottom)), m.Apply(new(r.Right, r.Top)), m.Apply(new(r.Left, r.Top)), m.Apply(new(r.Right, r.Bottom))]);

    /// <summary>회전 중심: 설명선은 글 상자 가운데, 나머지는 Rect 가운데.</summary>
    public static PdfPointF Pivot(Annotation a)
    {
        var b = a.Kind == AnnotKind.FreeText && a.Intent == FreeTextIntent.Callout && a.TextBox is { } tb ? tb : a.Rect;
        return new PdfPointF((b.Left + b.Right) / 2f, (b.Top + b.Bottom) / 2f);
    }

    /// <summary>쪽 좌표 → 바로 선 자기 좌표(Rotation의 반대로 돌림). Rotation이 0이면 a 그대로. 결과의 Rotation은 0.</summary>
    public static Annotation ToLocal(Annotation a, out PdfMatrix toPage)
    {
        int q = Quarter(a.Rotation);
        var c = Pivot(a);
        toPage = About(c, q);
        if (q == 0) return a;
        var inv = About(c, 4 - q);
        return a with
        {
            Rotation = 0,
            Rect = Apply(inv, a.Rect),
            TextBox = a.TextBox is { } tb ? Apply(inv, tb) : null,
            Vertices = a.Vertices.IsDefaultOrEmpty ? a.Vertices : [.. a.Vertices.Select(inv.Apply)],
        };
    }

    /// <summary>ToLocal로 얻은 자기 좌표의 주석(크기·위치를 바꾼 것)을 쪽 좌표로 되돌린다. Rotation은 original 값.</summary>
    public static Annotation FromLocal(Annotation local, PdfMatrix toPage, int rotation)
    {
        if (Quarter(rotation) == 0) return local with { Rotation = rotation };
        return local with
        {
            Rotation = rotation,
            Rect = Apply(toPage, local.Rect),
            TextBox = local.TextBox is { } tb ? Apply(toPage, tb) : null,
            Vertices = local.Vertices.IsDefaultOrEmpty ? local.Vertices : [.. local.Vertices.Select(toPage.Apply)],
        };
    }
}
