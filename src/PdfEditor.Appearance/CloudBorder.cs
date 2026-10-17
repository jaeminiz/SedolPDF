using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

/// <summary>클라우드 테두리(A8): 변마다 반원을 바깥쪽으로 이어 붙인 닫힌 경로. 반원 하나 = 베지어 2개.</summary>
internal static class CloudBorder
{
    /// <summary>변 하나의 반원 개수. 반원 지름이 d를 넘지 않도록 올림한다(볼록한 높이가 RD = d/2 + 두께/2 안에 들어오게).</summary>
    public static int ArcsPerEdge(float edgeLength, float diameter)
        => Math.Max(1, (int)Math.Ceiling(edgeLength / diameter - 1e-4f));

    /// <summary>Square의 클라우드 안쪽 사각형(= Rect를 RD만큼 줄임).</summary>
    public static ImmutableArray<PdfPointF> RectPolygon(PdfRectF rect, float intensity, float borderWidth)
    {
        float rd = AnnotationGeometry.CloudRectInset(rect, intensity, borderWidth);
        return [
            new(rect.Left + rd, rect.Bottom + rd), new(rect.Right - rd, rect.Bottom + rd),
            new(rect.Right - rd, rect.Top - rd), new(rect.Left + rd, rect.Top - rd),
        ];
    }

    public static ImmutableArray<PathSeg> Build(IReadOnlyList<PdfPointF> poly, float intensity)
    {
        var path = ImmutableArray.CreateBuilder<PathSeg>();
        int n = poly.Count;
        if (n < 3) return path.ToImmutable();
        float d = AnnotationGeometry.CloudDiameter(intensity);

        double area2 = 0;
        for (int i = 0; i < n; i++)
        {
            var p = poly[i]; var q = poly[(i + 1) % n];
            area2 += (double)p.X * q.Y - (double)q.X * p.Y;
        }
        // 반시계(넓이 > 0)면 진행 방향의 오른쪽이 바깥, 시계면 왼쪽이 바깥.
        float side = area2 >= 0 ? 1f : -1f;

        path.Add(new MoveTo(poly[0]));
        for (int i = 0; i < n; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % n];
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len <= 0f) continue;
            float ux = dx / len, uy = dy / len;
            float ox = uy * side, oy = -ux * side;
            int arcs = ArcsPerEdge(len, d);
            float step = len / arcs, r = step / 2f, kr = AnnotationPainter.Kappa * r;
            for (int j = 0; j < arcs; j++)
            {
                var p0 = new PdfPointF(a.X + ux * step * j, a.Y + uy * step * j);
                var p1 = new PdfPointF(p0.X + ux * step, p0.Y + uy * step);
                var apex = new PdfPointF(p0.X + ux * r + ox * r, p0.Y + uy * r + oy * r);
                path.Add(new CubicTo(
                    new(p0.X + ox * kr, p0.Y + oy * kr), new(apex.X - ux * kr, apex.Y - uy * kr), apex));
                path.Add(new CubicTo(
                    new(apex.X + ux * kr, apex.Y + uy * kr), new(p1.X + ox * kr, p1.Y + oy * kr), p1));
            }
        }
        path.Add(new ClosePath());
        return path.ToImmutable();
    }
}
