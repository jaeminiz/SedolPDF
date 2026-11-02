using System.Collections.Immutable;
using Pdf.Native;

namespace PdfEditor.Appearance;

/// <summary>이중선(0.5.0)용: 직선 경로를 양쪽으로 옮긴 두 경로. 화면과 AP가 같은 결과를 쓴다.</summary>
internal static class DoubleStroke
{
    private const float MiterLimitCos = 0.1f;   // 1 + n1·n2 가 이보다 작으면(거의 되접힘) 마디를 옮기지 않고 한쪽 법선만 쓴다

    /// <summary>
    /// path는 MoveTo/LineTo(+닫는 ClosePath)만 있는 직선 경로여야 한다. 곡선(구름)이 있으면 false.
    /// 점은 거리 d만큼 왼쪽(first)·오른쪽(second)으로 옮긴다(마디는 뾰족한 이음새로 계산).
    /// </summary>
    public static bool TryOffsets(ImmutableArray<PathSeg> path, float d, bool closed, out ImmutableArray<PathSeg> left, out ImmutableArray<PathSeg> right)
    {
        left = right = default;
        var pts = new List<PdfPointF>();
        foreach (var s in path)
        {
            switch (s)
            {
                case MoveTo m: pts.Add(m.P); break;
                case LineTo l: pts.Add(l.P); break;
                case ClosePath: break;
                default: return false;
            }
        }
        // 겹친 이웃 점 제거
        var u = new List<PdfPointF>();
        foreach (var p in pts) if (u.Count == 0 || Dist(u[^1], p) > 1e-4f) u.Add(p);
        if (closed && u.Count > 1 && Dist(u[0], u[^1]) <= 1e-4f) u.RemoveAt(u.Count - 1);
        if (u.Count < 2 || (closed && u.Count < 3)) return false;
        left = Build(u, d, closed);
        right = Build(u, -d, closed);
        return true;
    }

    private static float Dist(PdfPointF a, PdfPointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static (float X, float Y) Normal(PdfPointF a, PdfPointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, len = MathF.Sqrt(dx * dx + dy * dy);
        return (-dy / len, dx / len);   // 진행 방향의 왼쪽
    }

    private static ImmutableArray<PathSeg> Build(List<PdfPointF> p, float d, bool closed)
    {
        int n = p.Count;
        var b = ImmutableArray.CreateBuilder<PathSeg>();
        for (int i = 0; i < n; i++)
        {
            (float X, float Y) nPrev = i > 0 || closed ? Normal(p[(i - 1 + n) % n], p[i]) : default;
            (float X, float Y) nNext = i < n - 1 || closed ? Normal(p[i], p[(i + 1) % n]) : default;
            float ox, oy;
            if (!closed && i == 0) { ox = nNext.X * d; oy = nNext.Y * d; }
            else if (!closed && i == n - 1) { ox = nPrev.X * d; oy = nPrev.Y * d; }
            else
            {
                float k = 1f + nPrev.X * nNext.X + nPrev.Y * nNext.Y;
                if (k < MiterLimitCos) { ox = nNext.X * d; oy = nNext.Y * d; }
                else { ox = (nPrev.X + nNext.X) * d / k; oy = (nPrev.Y + nNext.Y) * d / k; }
            }
            var pt = new PdfPointF(p[i].X + ox, p[i].Y + oy);
            b.Add(i == 0 ? new MoveTo(pt) : new LineTo(pt));
        }
        if (closed) b.Add(new ClosePath());
        return b.ToImmutable();
    }
}
