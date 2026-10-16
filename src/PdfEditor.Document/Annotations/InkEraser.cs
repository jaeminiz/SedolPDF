using System.Collections.Immutable;
using Pdf.Native;

namespace PdfEditor.Document.Annotations;

/// <summary>연필 획을 원형 지우개로 잘라 내는 순수 계산 (PDF pt 좌표).</summary>
public static class InkEraser
{
    /// <summary>원 안에 들어간 부분을 지운다. 지워진 것이 없으면 같은 인스턴스를 돌려준다.</summary>
    public static ImmutableArray<ImmutableArray<PdfPointF>> EraseCircle(
        ImmutableArray<ImmutableArray<PdfPointF>> strokes, PdfPointF center, float radius)
    {
        var result = ImmutableArray.CreateBuilder<ImmutableArray<PdfPointF>>();
        bool changed = false;
        foreach (var stroke in strokes)
        {
            if (stroke.Length == 0) { result.Add(stroke); continue; }
            if (stroke.Length == 1)
            {
                if (Dist2(stroke[0], center) <= radius * radius) changed = true;
                else result.Add(stroke);
                continue;
            }
            var pieces = new List<List<PdfPointF>>();
            var cur = new List<PdfPointF>();
            bool strokeChanged = false;
            for (int i = 0; i + 1 < stroke.Length; i++)
            {
                var a = stroke[i];
                var b = stroke[i + 1];
                if (!TryInside(a, b, center, radius, out float lo, out float hi))
                {
                    if (cur.Count == 0) cur.Add(a);
                    cur.Add(b);
                    continue;
                }
                strokeChanged = true;
                if (lo > 0f)
                {
                    if (cur.Count == 0) cur.Add(a);
                    cur.Add(At(a, b, lo));
                }
                if (cur.Count >= 2) pieces.Add(cur);
                cur = [];
                if (hi < 1f)
                {
                    cur.Add(At(a, b, hi));
                    cur.Add(b);
                }
            }
            if (cur.Count >= 2) pieces.Add(cur);
            if (!strokeChanged) { result.Add(stroke); continue; }
            changed = true;
            foreach (var p in pieces) result.Add([.. p]);
        }
        return changed ? result.ToImmutable() : strokes;
    }

    /// <summary>from에서 to까지 끝점을 포함해 간격이 spacing 이하가 되도록 나눈다.</summary>
    public static IReadOnlyList<PdfPointF> SamplePath(PdfPointF from, PdfPointF to, float spacing)
    {
        float d = MathF.Sqrt(Dist2(from, to));
        var list = new List<PdfPointF> { from };
        if (d <= 0f) return list;
        int n = spacing <= 0f ? 1 : Math.Max(1, (int)MathF.Ceiling(d / spacing));
        for (int i = 1; i < n; i++) list.Add(At(from, to, (float)i / n));
        list.Add(to);
        return list;
    }

    private static PdfPointF At(PdfPointF a, PdfPointF b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static float Dist2(PdfPointF a, PdfPointF b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static bool TryInside(PdfPointF a, PdfPointF b, PdfPointF c, float r, out float lo, out float hi)
    {
        lo = hi = 0f;
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float fx = a.X - c.X, fy = a.Y - c.Y;
        float qa = dx * dx + dy * dy;
        if (qa <= 1e-12f)
        {
            if (fx * fx + fy * fy > r * r) return false;
            lo = 0f; hi = 1f;
            return true;
        }
        float qb = 2f * (fx * dx + fy * dy);
        float qc = fx * fx + fy * fy - r * r;
        float disc = qb * qb - 4f * qa * qc;
        if (disc <= 0f) return false;
        float s = MathF.Sqrt(disc);
        float t1 = (-qb - s) / (2f * qa);
        float t2 = (-qb + s) / (2f * qa);
        if (t2 < 0f || t1 > 1f) return false;
        lo = Math.Max(t1, 0f);
        hi = Math.Min(t2, 1f);
        return true;
    }
}
