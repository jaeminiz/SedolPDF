using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

/// <summary>선 끝 모양(A7). 크기 L은 <see cref="AnnotationGeometry.EndingSize"/> 하나만 쓴다(ComputeRect와 어긋나지 않게).</summary>
internal static class LineEndings
{
    private static readonly float Cos30 = MathF.Cos(MathF.PI / 6f);
    private const float Sin30 = 0.5f;
    private const float Cos60 = 0.5f;
    private static readonly float Sin60 = MathF.Sin(MathF.PI / 3f);

    /// <summary>본선을 끝점에서 안쪽으로 얼마나 짧게 그릴지(닫힌 모양 안으로 겹쳐 그리지 않기 위해).</summary>
    public static float ShaftInset(LineEnding e, float size) => e switch
    {
        LineEnding.ClosedArrow => size * Cos30,
        LineEnding.Circle or LineEnding.Square or LineEnding.Diamond => size / 2f,
        _ => 0f,
    };

    /// <param name="tip">선의 끝점</param>
    /// <param name="dir">선 방향 단위 벡터(안쪽에서 끝점 쪽, 즉 바깥을 향함)</param>
    public static void Add(List<PaintOp> ops, LineEnding e, PdfPointF tip, PdfPointF dir, float size, PdfColor color, PdfColor? fill, float width)
    {
        if (e == LineEnding.None || size <= 0f) return;
        PdfPointF At(float along, float across)
            => new(tip.X + dir.X * along - dir.Y * across, tip.Y + dir.Y * along + dir.X * across);
        float h = size / 2f;
        switch (e)
        {
            case LineEnding.OpenArrow:
                ops.Add(new PaintOp([
                    new MoveTo(At(-size * Cos30, size * Sin30)), new LineTo(tip), new LineTo(At(-size * Cos30, -size * Sin30)),
                ], null, color, width, LineCap.Butt, LineJoin.Round));
                break;
            case LineEnding.ClosedArrow:
                ops.Add(new PaintOp([
                    new MoveTo(tip), new LineTo(At(-size * Cos30, size * Sin30)), new LineTo(At(-size * Cos30, -size * Sin30)), new ClosePath(),
                ], fill ?? color, color, width, LineCap.Butt, LineJoin.Round));
                break;
            case LineEnding.Circle:
            {
                float k = AnnotationPainter.Kappa * h;
                var c = tip;
                ops.Add(new PaintOp([
                    new MoveTo(new(c.X + h, c.Y)),
                    new CubicTo(new(c.X + h, c.Y + k), new(c.X + k, c.Y + h), new(c.X, c.Y + h)),
                    new CubicTo(new(c.X - k, c.Y + h), new(c.X - h, c.Y + k), new(c.X - h, c.Y)),
                    new CubicTo(new(c.X - h, c.Y - k), new(c.X - k, c.Y - h), new(c.X, c.Y - h)),
                    new CubicTo(new(c.X + k, c.Y - h), new(c.X + h, c.Y - k), new(c.X + h, c.Y)),
                    new ClosePath(),
                ], fill, color, width, LineCap.Butt, LineJoin.Round));
                break;
            }
            case LineEnding.Square:
                ops.Add(new PaintOp([
                    new MoveTo(At(-h, -h)), new LineTo(At(h, -h)), new LineTo(At(h, h)), new LineTo(At(-h, h)), new ClosePath(),
                ], fill, color, width, LineCap.Butt, LineJoin.Round));
                break;
            case LineEnding.Diamond:
                ops.Add(new PaintOp([
                    new MoveTo(At(-h, 0)), new LineTo(At(0, -h)), new LineTo(At(h, 0)), new LineTo(At(0, h)), new ClosePath(),
                ], fill, color, width, LineCap.Butt, LineJoin.Round));
                break;
            case LineEnding.Butt:
                ops.Add(new PaintOp([new MoveTo(At(0, -h)), new LineTo(At(0, h))], null, color, width, LineCap.Butt, LineJoin.Round));
                break;
            case LineEnding.Slash:
                ops.Add(new PaintOp([
                    new MoveTo(At(-h * Cos60, -h * Sin60)), new LineTo(At(h * Cos60, h * Sin60)),
                ], null, color, width, LineCap.Butt, LineJoin.Round));
                break;
        }
    }
}
