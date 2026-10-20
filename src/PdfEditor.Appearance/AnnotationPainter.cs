using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

/// <summary>
/// 주석 모양을 그리기 목록으로 계산한다(PDF 사용자 좌표). 화면과 PDF AP가 같은 결과를 쓴다.
/// </summary>
public static class AnnotationPainter
{
    internal const float Kappa = 0.5522847498f;
    private static readonly PdfColor DefaultInk = new(0, 0, 0);
    private static readonly PdfColor DefaultTextIcon = new(0xFF, 0xD4, 0x00);
    private static readonly PdfColor TextIconLine = PdfColor.FromHex("#5C4A00");

    /// <summary>ctx는 글자가 있는 유형(FreeText, 글자·기본 스탬프)에, 그림 스탬프는 가로세로 비율을 위해 필요하다. 없으면 그 유형은 InvalidOperationException.</summary>
    public static AppearanceDrawing Paint(Annotation a, int pageRotation, PaintContext? ctx = null)
    {
        var ops = new List<PaintOp>();
        var texts = ImmutableArray<TextRunOp>.Empty;
        PdfRectF? textBounds = null;
        var images = ImmutableArray<ImageOp>.Empty;
        PdfMatrix transform = default;
        switch (a.Kind)
        {
            case AnnotKind.Ink: PaintInk(a, ops); break;
            case AnnotKind.Square: PaintSquare(a, ops); break;
            case AnnotKind.Circle: PaintCircle(a, ops); break;
            case AnnotKind.Highlight: PaintHighlight(a, ops); break;
            case AnnotKind.Underline: PaintLine(a, ops, 0.07f, underline: true); break;
            case AnnotKind.StrikeOut: PaintLine(a, ops, 0.07f, underline: false); break;
            case AnnotKind.Text: if (!a.IsThreadMember()) PaintTextIcon(a, pageRotation, ops); break;   // 답글·상태는 그리지 않는다(빈 AP, 계획 B1)
            case AnnotKind.Line:
            case AnnotKind.PolyLine: PaintPolyline(a, ops); break;
            case AnnotKind.Polygon: PaintPolygon(a, ops); break;
            case AnnotKind.Caret: PaintCaret(a, ops); break;
            case AnnotKind.FileAttachment: Icons.PaintAttachment(a, pageRotation, ops); break;
            // 글자가 있는 두 유형은 바로 선 자기 좌표에서 그리고 Rotation만큼 돌린다(회전된 쪽, 2026-10-20).
            case AnnotKind.FreeText: (texts, textBounds) = FreeTextPainter.Paint(AnnotationRotation.ToLocal(a, out transform), ctx, ops); break;
            case AnnotKind.Stamp: (texts, images, textBounds) = StampPainter.Paint(AnnotationRotation.ToLocal(a, out transform), ctx, ops); break;
        }
        var arr = ops.ToImmutableArray();
        var bounds = ComputeBounds(arr);
        if (textBounds is { } tb) bounds = arr.Length == 0 ? tb : bounds.Union(tb);
        bool rotated = a.Kind is AnnotKind.FreeText or AnnotKind.Stamp && AnnotationRotation.Quarter(a.Rotation) != 0;
        if (rotated) bounds = AnnotationRotation.Apply(transform, bounds);
        return new AppearanceDrawing(arr, a.Kind == AnnotKind.FreeText ? 1f : a.Opacity, bounds)
        {
            Texts = texts, Images = images, Transform = rotated ? transform : null,
        };
    }

    /// <summary>타자기 주석의 Rect를 글 배치 결과에 맞춘다(왼쪽 위 고정). 다른 유형은 그대로.</summary>
    public static Annotation FitTypewriter(Annotation a, PaintContext ctx) => FreeTextPainter.FitTypewriter(a, ctx);

    private static PdfRectF ComputeBounds(ImmutableArray<PaintOp> ops)
    {
        PdfRectF? total = null;
        foreach (var op in ops)
        {
            var pts = op.Path.SelectMany(PointsOf).ToList();
            if (pts.Count == 0) continue;
            float w = op.Stroke is null ? 0f : op.StrokeWidth / 2f;
            PdfRectF r;
            if (w > 0f && op.Cap == LineCap.Butt && op.Path.Length == 2 && op.Path[0] is MoveTo m0 && op.Path[1] is LineTo l1)
            {
                // Butt-capped single line: the stroke only extends perpendicular to the line, not past its ends.
                float dx = l1.P.X - m0.P.X, dy = l1.P.Y - m0.P.Y;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                float nx = len > 0f ? -dy / len * w : 0f, ny = len > 0f ? dx / len * w : 0f;
                r = PdfRectF.FromPoints([
                    new(m0.P.X + nx, m0.P.Y + ny), new(m0.P.X - nx, m0.P.Y - ny),
                    new(l1.P.X + nx, l1.P.Y + ny), new(l1.P.X - nx, l1.P.Y - ny)]);
            }
            else r = PdfRectF.FromPoints(pts).Inflate(w);
            total = total is null ? r : total.Value.Union(r);
        }
        return total ?? new PdfRectF(0, 0, 0, 0);
    }

    private static IEnumerable<PdfPointF> PointsOf(PathSeg s) => s switch
    {
        MoveTo m => [m.P],
        LineTo l => [l.P],
        CubicTo c => [c.C1, c.C2, c.P],
        _ => [],
    };

    private static void PaintInk(Annotation a, List<PaintOp> ops)
    {
        if (a.InkStrokes.IsDefaultOrEmpty) return;
        var color = a.Color ?? DefaultInk;
        foreach (var stroke in a.InkStrokes)
        {
            if (stroke.IsDefaultOrEmpty) continue;
            var path = ImmutableArray.CreateBuilder<PathSeg>();
            int n = stroke.Length;
            path.Add(new MoveTo(stroke[0]));
            if (n == 1)
            {
                path.Add(new LineTo(stroke[0]));
            }
            else if (n == 2)
            {
                path.Add(new LineTo(stroke[1]));
            }
            else
            {
                for (int i = 0; i < n - 1; i++)
                {
                    var pm = stroke[Math.Max(i - 1, 0)];
                    var p0 = stroke[i];
                    var p1 = stroke[i + 1];
                    var p2 = stroke[Math.Min(i + 2, n - 1)];
                    var c1 = new PdfPointF(p0.X + (p1.X - pm.X) / 6f, p0.Y + (p1.Y - pm.Y) / 6f);
                    var c2 = new PdfPointF(p1.X - (p2.X - p0.X) / 6f, p1.Y - (p2.Y - p0.Y) / 6f);
                    path.Add(new CubicTo(c1, c2, p1));
                }
            }
            ops.Add(new PaintOp(path.ToImmutable(), null, color, a.BorderWidth, LineCap.Round, LineJoin.Round));
        }
    }

    private static PdfRectF Inset(Annotation a)
    {
        float h = a.BorderWidth / 2f;
        return new PdfRectF(a.Rect.Left + h, a.Rect.Bottom + h, a.Rect.Right - h, a.Rect.Top - h);
    }

    private static void PaintSquare(Annotation a, List<PaintOp> ops)
    {
        var stroke = a.BorderWidth > 0 ? a.Color : null;
        if (a.FillColor is null && stroke is null) return;
        if (a.CloudIntensity > 0)
        {
            var cloud = CloudBorder.Build(CloudBorder.RectPolygon(a.Rect, a.CloudIntensity, a.BorderWidth), a.CloudIntensity);
            ops.Add(new PaintOp(cloud, a.FillColor, stroke, a.BorderWidth, LineCap.Butt, LineJoin.Round));
            return;
        }
        var r = Inset(a);
        ImmutableArray<PathSeg> path =
        [
            new MoveTo(new(r.Left, r.Bottom)),
            new LineTo(new(r.Right, r.Bottom)),
            new LineTo(new(r.Right, r.Top)),
            new LineTo(new(r.Left, r.Top)),
            new ClosePath(),
        ];
        ops.Add(new PaintOp(path, a.FillColor, stroke, a.BorderWidth, LineCap.Butt, LineJoin.Miter));
    }

    internal static void AddPolyline(Annotation a, List<PaintOp> ops) => PaintPolyline(a, ops);

    private static void PaintPolyline(Annotation a, List<PaintOp> ops)
    {
        var v = a.Vertices;
        if (v.IsDefaultOrEmpty || v.Length < 2 || a.BorderWidth <= 0f) return;
        var color = a.Color ?? DefaultInk;
        float size = AnnotationGeometry.EndingSize(a);
        int n = v.Length;
        var pts = v.ToArray();

        // Outward unit directions at both ends (zero when the end segment is degenerate).
        PdfPointF dirS = OutwardDir(v[0], v[1]), dirE = OutwardDir(v[n - 1], v[n - 2]);
        float insetS = LineEndings.ShaftInset(a.StartEnding, size), insetE = LineEndings.ShaftInset(a.EndEnding, size);
        if (n == 2)
        {
            float dx = v[1].X - v[0].X, dy = v[1].Y - v[0].Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (insetS + insetE >= len) { insetS = 0f; insetE = 0f; pts = []; }   // ending shapes cover the whole line
        }
        if (pts.Length > 0)
        {
            pts[0] = new(pts[0].X - dirS.X * insetS, pts[0].Y - dirS.Y * insetS);
            pts[^1] = new(pts[^1].X - dirE.X * insetE, pts[^1].Y - dirE.Y * insetE);
            var path = ImmutableArray.CreateBuilder<PathSeg>();
            path.Add(new MoveTo(pts[0]));
            for (int i = 1; i < pts.Length; i++) path.Add(new LineTo(pts[i]));
            ops.Add(new PaintOp(path.ToImmutable(), null, color, a.BorderWidth, LineCap.Butt, LineJoin.Round));
        }
        LineEndings.Add(ops, a.StartEnding, v[0], dirS, size, color, a.FillColor, a.BorderWidth);
        LineEndings.Add(ops, a.EndEnding, v[n - 1], dirE, size, color, a.FillColor, a.BorderWidth);
    }

    /// <summary>to에서 from쪽으로 향하는 단위 벡터. from이 선의 끝점이고 to가 그 다음 점이면 끝점에서 바깥으로 향한다(두 점이 같으면 0 벡터).</summary>
    private static PdfPointF OutwardDir(PdfPointF from, PdfPointF to)
    {
        float dx = from.X - to.X, dy = from.Y - to.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        return len > 0f ? new(dx / len, dy / len) : new(0, 0);
    }

    private static void PaintPolygon(Annotation a, List<PaintOp> ops)
    {
        var v = a.Vertices;
        if (v.IsDefaultOrEmpty || v.Length < 3) return;
        var stroke = a.BorderWidth > 0 ? a.Color ?? DefaultInk : (PdfColor?)null;
        if (a.FillColor is null && stroke is null) return;
        ImmutableArray<PathSeg> path;
        if (a.CloudIntensity > 0) path = CloudBorder.Build(v, a.CloudIntensity);
        else
        {
            var b = ImmutableArray.CreateBuilder<PathSeg>();
            b.Add(new MoveTo(v[0]));
            for (int i = 1; i < v.Length; i++) b.Add(new LineTo(v[i]));
            b.Add(new ClosePath());
            path = b.ToImmutable();
        }
        ops.Add(new PaintOp(path, a.FillColor, stroke, a.BorderWidth, LineCap.Butt, LineJoin.Round));
    }

    private static void PaintCaret(Annotation a, List<PaintOp> ops)
    {
        var r = a.Rect;
        ImmutableArray<PathSeg> path =
        [
            new MoveTo(new(r.Left, r.Bottom)), new LineTo(new(r.Right, r.Bottom)),
            new LineTo(new((r.Left + r.Right) / 2f, r.Top)), new ClosePath(),
        ];
        ops.Add(new PaintOp(path, a.Color ?? DefaultInk, null, 0f, LineCap.Butt, LineJoin.Miter));
    }

    private static void PaintCircle(Annotation a, List<PaintOp> ops)
    {
        var stroke = a.BorderWidth > 0 ? a.Color : null;
        if (a.FillColor is null && stroke is null) return;
        var r = Inset(a);
        float cx = (r.Left + r.Right) / 2f, cy = (r.Bottom + r.Top) / 2f;
        float rx = r.Width / 2f, ry = r.Height / 2f;
        float kx = Kappa * rx, ky = Kappa * ry;
        ImmutableArray<PathSeg> path =
        [
            new MoveTo(new(cx + rx, cy)),
            new CubicTo(new(cx + rx, cy + ky), new(cx + kx, cy + ry), new(cx, cy + ry)),
            new CubicTo(new(cx - kx, cy + ry), new(cx - rx, cy + ky), new(cx - rx, cy)),
            new CubicTo(new(cx - rx, cy - ky), new(cx - kx, cy - ry), new(cx, cy - ry)),
            new CubicTo(new(cx + kx, cy - ry), new(cx + rx, cy - ky), new(cx + rx, cy)),
            new ClosePath(),
        ];
        ops.Add(new PaintOp(path, a.FillColor, stroke, a.BorderWidth, LineCap.Butt, LineJoin.Miter));
    }

    private static void PaintHighlight(Annotation a, List<PaintOp> ops)
    {
        if (a.Quads.IsDefaultOrEmpty) return;
        var color = a.Color ?? DefaultInk;
        foreach (var q in a.Quads)
        {
            ImmutableArray<PathSeg> path =
            [
                new MoveTo(q.P1), new LineTo(q.P2), new LineTo(q.P4), new LineTo(q.P3), new ClosePath(),
            ];
            ops.Add(new PaintOp(path, color, null, 0f, LineCap.Butt, LineJoin.Miter));
        }
    }

    private static void PaintLine(Annotation a, List<PaintOp> ops, float widthRatio, bool underline)
    {
        if (a.Quads.IsDefaultOrEmpty) return;
        var color = a.Color ?? DefaultInk;
        foreach (var q in a.Quads)
        {
            float dx = q.P1.X - q.P3.X, dy = q.P1.Y - q.P3.Y;
            float h = MathF.Sqrt(dx * dx + dy * dy);
            if (h <= 0f) continue;
            float lw = MathF.Max(1f, widthRatio * h);
            PdfPointF from, to;
            if (underline)
            {
                float nx = dx / h, ny = dy / h;
                float off = lw / 2f + 0.04f * h;
                from = new(q.P3.X + nx * off, q.P3.Y + ny * off);
                to = new(q.P4.X + nx * off, q.P4.Y + ny * off);
            }
            else
            {
                from = new(q.P3.X + (q.P1.X - q.P3.X) * 0.45f, q.P3.Y + (q.P1.Y - q.P3.Y) * 0.45f);
                to = new(q.P4.X + (q.P2.X - q.P4.X) * 0.45f, q.P4.Y + (q.P2.Y - q.P4.Y) * 0.45f);
            }
            ops.Add(new PaintOp([new MoveTo(from), new LineTo(to)], null, color, lw, LineCap.Butt, LineJoin.Miter));
        }
    }

    private static void PaintTextIcon(Annotation a, int pageRotation, List<PaintOp> ops)
    {
        var fill = a.Color ?? DefaultTextIcon;
        var edge = new PdfColor(Scale(fill.R), Scale(fill.G), Scale(fill.B));
        // Icon coordinates (u, v) have the origin at the top-left of the 20x20 box with v pointing down (see Icons.Mapper).
        var mapper = Icons.Mapper(a.Rect, pageRotation);
        PdfPointF M(float u, float v) => mapper(u, v);

        const float lo = 1f, hi = 19f, r = 2.5f, k = Kappa * r;
        ImmutableArray<PathSeg> body =
        [
            new MoveTo(M(lo + r, lo)),
            new LineTo(M(hi - r, lo)),
            new CubicTo(M(hi - r + k, lo), M(hi, lo + r - k), M(hi, lo + r)),
            new LineTo(M(hi, hi - r)),
            new CubicTo(M(hi, hi - r + k), M(hi - r + k, hi), M(hi - r, hi)),
            new LineTo(M(lo + r, hi)),
            new CubicTo(M(lo + r - k, hi), M(lo, hi - r + k), M(lo, hi - r)),
            new LineTo(M(lo, lo + r)),
            new CubicTo(M(lo, lo + r - k), M(lo + r - k, lo), M(lo + r, lo)),
            new ClosePath(),
        ];
        ops.Add(new PaintOp(body, fill, edge, 0.8f, LineCap.Butt, LineJoin.Round));

        (float u0, float u1, float v)[] lines = [(5f, 15f, 6.5f), (5f, 15f, 10f), (5f, 11.5f, 13.5f)];
        foreach (var (u0, u1, v) in lines)
            ops.Add(new PaintOp([new MoveTo(M(u0, v)), new LineTo(M(u1, v))], null, TextIconLine, 0.9f, LineCap.Round, LineJoin.Round));
    }

    private static byte Scale(byte c) => (byte)MathF.Round(c * 0.75f);
}
