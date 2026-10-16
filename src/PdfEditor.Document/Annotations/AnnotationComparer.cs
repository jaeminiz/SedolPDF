using Pdf.Native;

namespace PdfEditor.Document.Annotations;

/// <summary>Id·시각을 뺀 내용 비교. ImmutableArray의 기본 비교는 참조 비교라 따로 만든다.</summary>
public static class AnnotationComparer
{
    public static bool Equivalent(Annotation a, Annotation b, float tol = 0.01f)
    {
        if (a.Kind != b.Kind || a.Color != b.Color || a.FillColor != b.FillColor
            || a.Contents != b.Contents || a.Author != b.Author || a.Subject != b.Subject
            || a.Name != b.Name || a.Flags != b.Flags)
            return false;
        if (!Near(a.Opacity, b.Opacity, tol) || !Near(a.BorderWidth, b.BorderWidth, tol)) return false;
        if (!Near(a.Rect, b.Rect, tol)) return false;

        var sa = a.InkStrokes.IsDefault ? [] : a.InkStrokes;
        var sb = b.InkStrokes.IsDefault ? [] : b.InkStrokes;
        if (sa.Length != sb.Length) return false;
        for (int i = 0; i < sa.Length; i++)
        {
            if (sa[i].Length != sb[i].Length) return false;
            for (int j = 0; j < sa[i].Length; j++)
                if (!Near(sa[i][j], sb[i][j], tol)) return false;
        }

        var qa = a.Quads.IsDefault ? [] : a.Quads;
        var qb = b.Quads.IsDefault ? [] : b.Quads;
        if (qa.Length != qb.Length) return false;
        for (int i = 0; i < qa.Length; i++)
            if (!Near(qa[i].P1, qb[i].P1, tol) || !Near(qa[i].P2, qb[i].P2, tol)
                || !Near(qa[i].P3, qb[i].P3, tol) || !Near(qa[i].P4, qb[i].P4, tol))
                return false;
        return true;
    }

    private static bool Near(float x, float y, float tol) => Math.Abs(x - y) <= tol;
    private static bool Near(PdfPointF p, PdfPointF q, float tol) => Near(p.X, q.X, tol) && Near(p.Y, q.Y, tol);
    private static bool Near(PdfRectF r, PdfRectF s, float tol)
        => Near(r.Left, s.Left, tol) && Near(r.Bottom, s.Bottom, tol) && Near(r.Right, s.Right, tol) && Near(r.Top, s.Top, tol);
}
