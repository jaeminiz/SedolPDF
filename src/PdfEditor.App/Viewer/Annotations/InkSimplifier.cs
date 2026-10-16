using Pdf.Native;

namespace PdfEditor.App.Viewer.Annotations;

/// <summary>Ramer–Douglas–Peucker 점 줄이기. 처음·끝 점은 유지한다.</summary>
public static class InkSimplifier
{
    public static IReadOnlyList<PdfPointF> Simplify(IReadOnlyList<PdfPointF> points, float tolerance)
    {
        if (points.Count <= 2) return points;
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            float max = -1; int idx = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = Distance(points[i], points[a], points[b]);
                if (d > max) { max = d; idx = i; }
            }
            if (idx >= 0 && max > tolerance)
            {
                keep[idx] = true;
                stack.Push((a, idx));
                stack.Push((idx, b));
            }
        }
        var result = new List<PdfPointF>();
        for (int i = 0; i < points.Count; i++) if (keep[i]) result.Add(points[i]);
        return result;
    }

    private static float Distance(PdfPointF p, PdfPointF a, PdfPointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len2 = dx * dx + dy * dy;
        if (len2 == 0) return MathF.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        return MathF.Abs(dy * (p.X - a.X) - dx * (p.Y - a.Y)) / MathF.Sqrt(len2);
    }
}
