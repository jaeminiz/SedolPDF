using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using PdfEditor.Appearance;
using PdfEditor.Document.Annotations;
using AvLineCap = Avalonia.Media.PenLineCap;
using AvLineJoin = Avalonia.Media.PenLineJoin;

namespace PdfEditor.App.Viewer.Annotations;

/// <summary>한 주석의 화면용 그리기 항목 목록. 경로는 쪽 pt 좌표 그대로(변환 행렬이 화면으로 옮긴다).</summary>
public sealed record CachedAppearance(IReadOnlyList<(Geometry Geometry, IBrush? Fill, IPen? Stroke)> Items);

/// <summary>
/// <see cref="AnnotationPainter"/>의 <see cref="AppearanceDrawing"/>을 Avalonia 도형으로 바꾼다. 모양 계산은 하지 않는다.
/// 주석 인스턴스(변경하면 새 인스턴스) × 쪽 회전마다 한 번만 만든다. UI 스레드에서 쓴다.
/// </summary>
public static class AppearanceGeometry
{
    private static readonly ConditionalWeakTable<Annotation, Dictionary<int, CachedAppearance>> Cache = new();

    public static CachedAppearance Get(Annotation a, int pageRotation)
    {
        int rot = ((pageRotation % 4) + 4) % 4;
        var perRot = Cache.GetValue(a, _ => new Dictionary<int, CachedAppearance>());
        lock (perRot)
        {
            if (!perRot.TryGetValue(rot, out var c))
                perRot[rot] = c = Build(AnnotationPainter.Paint(a, rot));
            return c;
        }
    }

    private static CachedAppearance Build(AppearanceDrawing d)
    {
        var items = new List<(Geometry, IBrush?, IPen?)>(d.Ops.Length);
        // PDF의 /GS gs처럼 Op마다 알파를 곱한다(그룹 투명도가 아님): 채움과 선이 겹쳐도 각자 알파로 칠해진다.
        byte alpha = (byte)Math.Clamp((int)Math.Round(d.Opacity * 255f), 0, 255);
        foreach (var op in d.Ops)
        {
            var geo = ToGeometry(op);
            IBrush? fill = op.Fill is { } f ? new ImmutableSolidColorBrush(Color.FromArgb(alpha, f.R, f.G, f.B)) : null;
            IPen? stroke = null;
            if (op.Stroke is { } s)
            {
                var sb = new ImmutableSolidColorBrush(Color.FromArgb(alpha, s.R, s.G, s.B));
                stroke = new ImmutablePen(sb, op.StrokeWidth, null, ToCap(op.Cap), ToJoin(op.Join));
            }
            items.Add((geo, fill, stroke));
        }
        return new CachedAppearance(items);
    }

    private static Geometry ToGeometry(PaintOp op)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.SetFillRule(FillRule.NonZero);   // PDF `f` = 비영 감기
            bool open = false;
            foreach (var seg in op.Path)
            {
                switch (seg)
                {
                    case MoveTo m:
                        if (open) ctx.EndFigure(false);
                        ctx.BeginFigure(new Point(m.P.X, m.P.Y), op.Fill is not null);
                        open = true;
                        break;
                    case LineTo l when open:
                        ctx.LineTo(new Point(l.P.X, l.P.Y));
                        break;
                    case CubicTo c when open:
                        ctx.CubicBezierTo(new Point(c.C1.X, c.C1.Y), new Point(c.C2.X, c.C2.Y), new Point(c.P.X, c.P.Y));
                        break;
                    case ClosePath when open:
                        ctx.EndFigure(true);
                        open = false;
                        break;
                }
            }
            if (open) ctx.EndFigure(false);
        }
        return g;
    }

    private static AvLineCap ToCap(LineCap c) => c switch
    {
        LineCap.Round => AvLineCap.Round,
        LineCap.Square => AvLineCap.Square,
        _ => AvLineCap.Flat,
    };

    private static AvLineJoin ToJoin(LineJoin j) => j switch
    {
        LineJoin.Round => AvLineJoin.Round,
        LineJoin.Bevel => AvLineJoin.Bevel,
        _ => AvLineJoin.Miter,
    };
}
