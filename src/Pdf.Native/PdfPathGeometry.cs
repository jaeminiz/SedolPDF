namespace Pdf.Native;

/// <summary>경로의 부분 경로 하나(쪽 좌표 pt 꼭짓점, 곡선은 선분으로 편 것). Closed면 끝점에서 시작점으로 닫는다.</summary>
public sealed record PdfSubpath(PdfPointF[] Points, bool Closed);

/// <summary>
/// 경로 객체의 모양(쪽 좌표 pt). 화면 hit test용: 선만 긋는 경로는 경계 상자 안이 아니라 실제 선 근처만 맞고,
/// 칠하는 경로는 안쪽도 맞는다. 순수 계산이라 UI 스레드에서 써도 된다.
/// </summary>
public sealed record PdfPathGeometry(IReadOnlyList<PdfSubpath> Subpaths, bool Filled, bool Stroked, float StrokeWidthPt)
{
    /// <summary>점 p가 이 경로를 눌렀다고 볼 수 있나. tolerancePt는 화면 픽셀 기준 허용 오차(확대하면 작아진다).</summary>
    public bool Hit(PdfPointF p, float tolerancePt)
    {
        // 선: 칠하지 않고 그리기도 안 하는 경로(클리핑 전용 등)는 선으로도 본다 — 눌러도 아무것도 안 맞는 것보다 낫다
        bool strokeLike = Stroked || !Filled;
        float reach = tolerancePt + (Stroked ? StrokeWidthPt / 2f : 0f);
        float reach2 = reach * reach;
        foreach (var sp in Subpaths)
        {
            var pts = sp.Points;
            if (strokeLike)
            {
                for (int i = 0; i + 1 < pts.Length; i++)
                    if (DistSq(p, pts[i], pts[i + 1]) <= reach2) return true;
                if (pts.Length == 1 && Dist2(p, pts[0]) <= reach2) return true;
                if ((sp.Closed || Filled) && pts.Length > 2 && DistSq(p, pts[^1], pts[0]) <= reach2) return true;
            }
        }
        // 칠한 경로: 안쪽(짝홀 규칙 — 모든 부분 경로를 합쳐서 구멍도 반영)
        if (Filled)
        {
            bool inside = false;
            foreach (var sp in Subpaths)
            {
                var pts = sp.Points;
                if (pts.Length < 3) continue;
                for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
                    if ((pts[i].Y > p.Y) != (pts[j].Y > p.Y)
                        && p.X < (pts[j].X - pts[i].X) * (p.Y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X)
                        inside = !inside;
            }
            if (inside) return true;
            if (!strokeLike)
            {
                // 칠하기만 하는 경로도 가장자리 허용 오차 안이면 맞는다(얇은 도형 클릭 편의)
                foreach (var sp in Subpaths)
                {
                    var pts = sp.Points;
                    for (int i = 0; i + 1 < pts.Length; i++)
                        if (DistSq(p, pts[i], pts[i + 1]) <= tolerancePt * tolerancePt) return true;
                    if (pts.Length > 2 && DistSq(p, pts[^1], pts[0]) <= tolerancePt * tolerancePt) return true;
                }
            }
        }
        return false;
    }

    private static float Dist2(PdfPointF a, PdfPointF b) { float dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }

    /// <summary>점 p와 선분 ab 사이 거리의 제곱.</summary>
    private static float DistSq(PdfPointF p, PdfPointF a, PdfPointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len2 = dx * dx + dy * dy;
        if (len2 <= 1e-12f) return Dist2(p, a);
        float t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0f, 1f);
        return Dist2(p, new PdfPointF(a.X + t * dx, a.Y + t * dy));
    }
}
