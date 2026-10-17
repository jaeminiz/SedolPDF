using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

/// <summary>20x20 아이콘 주석(메모·파일 첨부)의 공통 처리. 아이콘은 쪽 회전과 상관없이 화면에서 바로 서 보인다.</summary>
internal static class Icons
{
    private static readonly PdfColor DefaultClip = new(0x33, 0x33, 0x33);

    /// <summary>아이콘 좌표(u, v: 왼쪽 위 원점, v는 아래로)를 쪽 좌표로. 쪽 회전의 반대로 돌려 바로 세운다.</summary>
    public static Func<float, float, PdfPointF> Mapper(PdfRectF rect, int pageRotation)
    {
        int rot = ((pageRotation % 4) + 4) % 4;
        float cx = (rect.Left + rect.Right) / 2f, cy = (rect.Bottom + rect.Top) / 2f;
        return (u, v) =>
        {
            float x = rect.Left + u - cx, y = rect.Top - v - cy;
            (float rx, float ry) = rot switch
            {
                0 => (x, y),
                1 => (-y, x),
                2 => (-x, -y),
                _ => (y, -x),
            };
            return new PdfPointF(cx + rx, cy + ry);
        };
    }

    /// <summary>클립 모양(선 두께 1.2, Round, Stroke = Color).</summary>
    public static void PaintAttachment(Annotation a, int pageRotation, List<PaintOp> ops)
    {
        var m = Mapper(a.Rect, pageRotation);
        var path = ImmutableArray.CreateBuilder<PathSeg>();
        path.Add(new MoveTo(m(8, 13)));
        path.Add(new LineTo(m(8, 6)));
        Semi(path, m, 10, 6, 2, +1, -1);     // 안쪽 위 반원: (8,6) -> (12,6)
        path.Add(new LineTo(m(12, 15)));
        Semi(path, m, 9, 15, 3, -1, +1);     // 아래 반원: (12,15) -> (6,15)
        path.Add(new LineTo(m(6, 5.5f)));
        Semi(path, m, 10, 5.5f, 4, +1, -1);  // 바깥 위 반원: (6,5.5) -> (14,5.5)
        path.Add(new LineTo(m(14, 14)));
        ops.Add(new PaintOp(path.ToImmutable(), null, a.Color ?? DefaultClip, 1.2f, LineCap.Round, LineJoin.Round));
    }

    // dir = +1: 왼쪽에서 오른쪽, -1: 오른쪽에서 왼쪽. vs = -1: 위로 볼록, +1: 아래로 볼록 (v는 아래로 증가).
    private static void Semi(ImmutableArray<PathSeg>.Builder path, Func<float, float, PdfPointF> m,
        float cx, float cy, float r, int dir, int vs)
    {
        float k = AnnotationPainter.Kappa * r;
        path.Add(new CubicTo(m(cx - dir * r, cy + vs * k), m(cx - dir * k, cy + vs * r), m(cx, cy + vs * r)));
        path.Add(new CubicTo(m(cx + dir * k, cy + vs * r), m(cx + dir * r, cy + vs * k), m(cx + dir * r, cy)));
    }
}
