using Pdf.Native;

namespace PdfEditor.Document.Annotations;

public static class AnnotationHitTest
{
    public static bool Hit(Annotation a, PdfPointF p, float tolPt)
    {
        float half = a.BorderWidth / 2f;
        switch (a.Kind)
        {
            case AnnotKind.Ink:
                return InkHit(a, p, half + tolPt);
            case AnnotKind.Square:
                {
                    if (a.FillColor is not null && a.Rect.Inflate(tolPt).Contains(p)) return true;
                    var inner = a.Rect.Inflate(-half);
                    return DistToRectBorder(inner, p) <= half + tolPt;
                }
            case AnnotKind.Circle:
                {
                    var inner = a.Rect.Inflate(-half);
                    double rx = inner.Width / 2.0, ry = inner.Height / 2.0;
                    if (rx <= 0 || ry <= 0) return a.Rect.Inflate(tolPt).Contains(p);
                    double nx = (p.X - (inner.Left + inner.Right) / 2.0) / rx;
                    double ny = (p.Y - (inner.Bottom + inner.Top) / 2.0) / ry;
                    double d = Math.Sqrt(nx * nx + ny * ny);
                    if (a.FillColor is not null && d <= 1.0 + tolPt / Math.Min(rx, ry)) return true;
                    return Math.Abs(d - 1.0) * Math.Min(rx, ry) <= half + tolPt;
                }
            case AnnotKind.Highlight:
            case AnnotKind.Underline:
            case AnnotKind.StrikeOut:
                if (a.Quads.IsDefaultOrEmpty) return a.Rect.Inflate(tolPt).Contains(p);
                foreach (var q in a.Quads)
                    if (q.Bounds.Inflate(tolPt).Contains(p)) return true;
                return false;
            case AnnotKind.Line:
            case AnnotKind.PolyLine:
                return PolylineHit(a.Vertices, closed: false, p, half + tolPt, a.Rect, tolPt);
            case AnnotKind.Polygon:
                if (a.Vertices.IsDefaultOrEmpty) return a.Rect.Inflate(tolPt).Contains(p);
                if (a.FillColor is not null && a.Vertices.Length >= 3 && PointInPolygon(a.Vertices, p)) return true;
                return PolylineHit(a.Vertices, closed: true, p, half + tolPt, a.Rect, tolPt);
            case AnnotKind.FreeText:
                {
                    if ((a.TextBox ?? a.Rect).Inflate(tolPt).Contains(p)) return true;
                    return a.Vertices.Length > 1 && PolylineHit(a.Vertices, closed: false, p, half + tolPt, a.Rect, tolPt);
                }
            default:
                return a.Rect.Inflate(tolPt).Contains(p);
        }
    }

    /// <summary>목록 뒤쪽(나중에 그린 것)부터 검사해 가장 위의 주석을 돌려준다.</summary>
    public static AnnotId? TopmostAt(IReadOnlyList<Annotation> drawOrder, PdfPointF p, float tolPt)
    {
        for (int i = drawOrder.Count - 1; i >= 0; i--)
            if (Hit(drawOrder[i], p, tolPt)) return drawOrder[i].Id;
        return null;
    }

    private static bool InkHit(Annotation a, PdfPointF p, float limit)
    {
        if (a.InkStrokes.IsDefault) return false;
        foreach (var s in a.InkStrokes)
        {
            if (s.Length == 1 && Dist(p, s[0]) <= limit) return true;
            for (int i = 1; i < s.Length; i++)
                if (DistToSegment(p, s[i - 1], s[i]) <= limit) return true;
        }
        return false;
    }

    private static bool PolylineHit(System.Collections.Immutable.ImmutableArray<PdfPointF> v, bool closed, PdfPointF p, float limit,
        PdfRectF fallback, float tolPt)
    {
        if (v.IsDefaultOrEmpty) return fallback.Inflate(tolPt).Contains(p);
        if (v.Length == 1) return Dist(p, v[0]) <= limit;
        for (int i = 1; i < v.Length; i++)
            if (DistToSegment(p, v[i - 1], v[i]) <= limit) return true;
        return closed && v.Length > 2 && DistToSegment(p, v[^1], v[0]) <= limit;
    }

    private static bool PointInPolygon(System.Collections.Immutable.ImmutableArray<PdfPointF> v, PdfPointF p)
    {
        bool inside = false;
        for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
        {
            if ((v[i].Y > p.Y) != (v[j].Y > p.Y)
                && p.X < (v[j].X - v[i].X) * (double)(p.Y - v[i].Y) / (v[j].Y - v[i].Y) + v[i].X)
                inside = !inside;
        }
        return inside;
    }

    private static double Dist(PdfPointF a, PdfPointF b)
        => Math.Sqrt((a.X - b.X) * (double)(a.X - b.X) + (a.Y - b.Y) * (double)(a.Y - b.Y));

    private static double DistToSegment(PdfPointF p, PdfPointF a, PdfPointF b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 == 0) return Dist(p, a);
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return Math.Sqrt(Math.Pow(p.X - (a.X + t * dx), 2) + Math.Pow(p.Y - (a.Y + t * dy), 2));
    }

    private static double DistToRectBorder(PdfRectF r, PdfPointF p)
    {
        var c = new[]
        {
            new PdfPointF(r.Left, r.Bottom), new PdfPointF(r.Right, r.Bottom),
            new PdfPointF(r.Right, r.Top), new PdfPointF(r.Left, r.Top),
        };
        double best = double.MaxValue;
        for (int i = 0; i < 4; i++) best = Math.Min(best, DistToSegment(p, c[i], c[(i + 1) % 4]));
        return best;
    }
}
