using System.Collections.Immutable;
using Pdf.Native;

namespace PdfEditor.Document.Annotations;

/// <summary>
/// 주석 회전(0.10.0, 설계서 6절). 순수 함수 — 점으로 모양이 정해지는 주석은 자유 각도로,
/// 상자로 모양이 정해지는 주석(사각형·원·텍스트 상자·스탬프)은 90° 단위로만 돌린다. 각도는 쪽 좌표에서 반시계가 +(객체 회전과 같다).
/// </summary>
public static class AnnotationRotate
{
    /// <summary>형광펜·밑줄·취소선(글에 붙음), 메모·캐럿·첨부(아이콘)는 돌리지 않는다.</summary>
    public static bool CanRotate(Annotation a) => a.Kind is not (AnnotKind.Highlight or AnnotKind.Underline or AnnotKind.StrikeOut
        or AnnotKind.Text or AnnotKind.Caret or AnnotKind.FileAttachment);

    /// <summary>상자 모양이라 90° 배수로만 돌아가는 종류.</summary>
    public static bool SnapsToQuarter(Annotation a) => a.Kind is AnnotKind.Square or AnnotKind.Circle or AnnotKind.FreeText or AnnotKind.Stamp;

    /// <summary>실제로 적용되는 각도(라디안). 자유 각도 종류는 그대로, 상자 종류는 가장 가까운 90° 배수(딱 가운데면 0에서 먼 쪽).</summary>
    public static float EffectiveAngle(Annotation a, float angleRad)
        => SnapsToQuarter(a) ? Quarters(angleRad) * (MathF.PI / 2f) : angleRad;

    /// <summary>각도를 가장 가까운 90° 배수 개수로(부호 있음). 부동소수 오차로 정확히 45°가 아래로 떨어지지 않게 아주 작은 여유를 둔다.</summary>
    private static int Quarters(float angleRad)
    {
        double k = angleRad / (Math.PI / 2);
        return (int)Math.Round(k + Math.Sign(k) * 1e-4, MidpointRounding.AwayFromZero);
    }

    /// <summary>center를 중심으로 angleRad만큼 돌린 주석. 돌릴 수 없는 종류면 null.</summary>
    public static Annotation? Apply(Annotation a, float angleRad, PdfPointF center)
    {
        if (!CanRotate(a)) return null;
        if (!SnapsToQuarter(a)) return RotatePoints(a, angleRad, center);

        int q = ((Quarters(angleRad) % 4) + 4) % 4;
        float eff = EffectiveAngle(a, angleRad);
        if (a.Kind == AnnotKind.FreeText && a.Intent == FreeTextIntent.Callout)
        {
            // 설명선: 지시선 점은 그대로 돌리고(90° 배수), 글 상자는 텍스트 상자처럼 다룬다. Rect는 글 상자와 지시선을 합쳐 다시 계산한다.
            var box = a.TextBox ?? a.Rect;
            var callout = a with
            {
                Vertices = Rot(a.Vertices, eff, center), Leader2 = Rot(a.Leader2, eff, center),
                TextBox = RotateBox(box, eff, q, center),
                Rotation = (a.Rotation + q * 90) % 360,
            };
            return callout with { Rect = AnnotationGeometry.ComputeRect(callout) };
        }
        var rotated = a with { Rect = RotateBox(a.Rect, eff, q, center) };
        if (a.Kind is AnnotKind.FreeText or AnnotKind.Stamp) rotated = rotated with { Rotation = (a.Rotation + q * 90) % 360 };
        return rotated;
    }

    private static Annotation RotatePoints(Annotation a, float angle, PdfPointF c)
    {
        var rotated = a with
        {
            Vertices = Rot(a.Vertices, angle, c),
            Leader2 = Rot(a.Leader2, angle, c),
            InkStrokes = a.InkStrokes.IsDefaultOrEmpty ? a.InkStrokes : [.. a.InkStrokes.Select(s => Rot(s, angle, c))],
        };
        return rotated with { Rect = AnnotationGeometry.ComputeRect(rotated) };
    }

    /// <summary>상자의 가운데를 center 둘레로 돌리고, 90°·270°면 너비·높이를 바꿔 그 가운데에 놓는다.</summary>
    private static PdfRectF RotateBox(PdfRectF b, float eff, int q, PdfPointF center)
    {
        var mid = Rot(new PdfPointF((b.Left + b.Right) / 2f, (b.Bottom + b.Top) / 2f), eff, center);
        float w = b.Width, h = b.Height;
        if (q % 2 == 1) (w, h) = (h, w);
        return new PdfRectF(mid.X - w / 2f, mid.Y - h / 2f, mid.X + w / 2f, mid.Y + h / 2f);
    }

    private static ImmutableArray<PdfPointF> Rot(ImmutableArray<PdfPointF> pts, float angle, PdfPointF c)
        => pts.IsDefaultOrEmpty ? pts : [.. pts.Select(p => Rot(p, angle, c))];

    private static PdfPointF Rot(PdfPointF p, float angle, PdfPointF c)
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle), dx = p.X - c.X, dy = p.Y - c.Y;
        return new PdfPointF((float)(c.X + dx * cos - dy * sin), (float)(c.Y + dx * sin + dy * cos));
    }
}
