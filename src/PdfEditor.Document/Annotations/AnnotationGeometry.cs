using System.Collections.Immutable;
using Pdf.Native;

namespace PdfEditor.Document.Annotations;

/// <summary>주석 모양 계산. 순수 함수라 스레드와 무관하다.</summary>
public static class AnnotationGeometry
{
    public const float TextIconSizePt = 20f;
    public const float MinShapeSizePt = 2f;

    private static float InkMargin(Annotation a) => a.BorderWidth / 2f + 1f;

    public static PdfRectF ComputeRect(Annotation a)
    {
        switch (a.Kind)
        {
            case AnnotKind.Ink:
                {
                    var pts = InkPoints(a);
                    return pts.Count == 0 ? a.Rect : PdfRectF.FromPoints(pts).Inflate(InkMargin(a));
                }
            case AnnotKind.Highlight:
            case AnnotKind.Underline:
            case AnnotKind.StrikeOut:
                {
                    if (a.Quads.IsDefaultOrEmpty) return a.Rect;
                    var r = a.Quads[0].Bounds;
                    for (int i = 1; i < a.Quads.Length; i++) r = r.Union(a.Quads[i].Bounds);
                    return r;
                }
            default:
                return a.Rect;
        }
    }

    public static Annotation Translate(Annotation a, float dx, float dy)
    {
        var strokes = a.InkStrokes.IsDefault ? a.InkStrokes
            : [.. a.InkStrokes.Select(s => ImmutableArray.CreateRange(s.Select(p => new PdfPointF(p.X + dx, p.Y + dy))))];
        var quads = a.Quads.IsDefault ? a.Quads
            : [.. a.Quads.Select(q => new PdfQuadF(Move(q.P1, dx, dy), Move(q.P2, dx, dy), Move(q.P3, dx, dy), Move(q.P4, dx, dy)))];
        return a with { Rect = a.Rect.Offset(dx, dy), InkStrokes = strokes, Quads = quads };
    }

    public static bool CanResize(AnnotKind k) => k is AnnotKind.Ink or AnnotKind.Square or AnnotKind.Circle;

    public static Annotation Resize(Annotation a, PdfRectF newRect)
    {
        if (!CanResize(a.Kind)) throw new NotSupportedException($"{a.Kind} annotations cannot be resized.");
        newRect = ClampMin(newRect);
        if (a.Kind != AnnotKind.Ink) return a with { Rect = newRect };

        var pts = InkPoints(a);
        if (pts.Count == 0) return a with { Rect = newRect };

        float m = InkMargin(a);
        var old = PdfRectF.FromPoints(pts);
        var dst = newRect.Inflate(-m);
        if (dst.Width < 0)
        {
            float cx = (dst.Left + dst.Right) / 2;
            dst = new PdfRectF(cx, dst.Bottom, cx, dst.Top);
        }
        if (dst.Height < 0)
        {
            float cy = (dst.Bottom + dst.Top) / 2;
            dst = new PdfRectF(dst.Left, cy, dst.Right, cy);
        }

        PdfPointF Map(PdfPointF p) => new(
            old.Width > 0 ? dst.Left + (p.X - old.Left) / old.Width * dst.Width : dst.Left,
            old.Height > 0 ? dst.Bottom + (p.Y - old.Bottom) / old.Height * dst.Height : dst.Bottom);

        var strokes = ImmutableArray.CreateRange(a.InkStrokes.Select(s => ImmutableArray.CreateRange(s.Select(Map))));
        var resized = a with { InkStrokes = strokes };
        return resized with { Rect = ComputeRect(resized) };
    }

    private static PdfRectF ClampMin(PdfRectF r)
    {
        float w = Math.Max(r.Width, MinShapeSizePt);
        float h = Math.Max(r.Height, MinShapeSizePt);
        return new PdfRectF(r.Left, r.Bottom, r.Left + w, r.Bottom + h);
    }

    private static PdfPointF Move(PdfPointF p, float dx, float dy) => new(p.X + dx, p.Y + dy);

    internal static List<PdfPointF> InkPoints(Annotation a)
    {
        var list = new List<PdfPointF>();
        if (a.InkStrokes.IsDefault) return list;
        foreach (var s in a.InkStrokes) list.AddRange(s);
        return list;
    }
}
