using System.Drawing;

namespace Pdf.Native;

/// <summary>PDF 사용자 좌표(pt, 원점 왼쪽 아래, y 위쪽)의 점.</summary>
public readonly record struct PdfPointF(float X, float Y);

/// <summary>PDF 사용자 좌표의 사각형. Bottom &lt;= Top (y 위쪽).</summary>
public readonly record struct PdfRectF(float Left, float Bottom, float Right, float Top)
{
    public float Width => Right - Left;
    public float Height => Top - Bottom;

    public bool Contains(PdfPointF p) => p.X >= Left && p.X <= Right && p.Y >= Bottom && p.Y <= Top;

    public PdfRectF Inflate(float d) => new(Left - d, Bottom - d, Right + d, Top + d);

    public PdfRectF Offset(float dx, float dy) => new(Left + dx, Bottom + dy, Right + dx, Top + dy);

    public PdfRectF Union(PdfRectF o)
        => new(Math.Min(Left, o.Left), Math.Min(Bottom, o.Bottom), Math.Max(Right, o.Right), Math.Max(Top, o.Top));

    public static PdfRectF FromPoints(IEnumerable<PdfPointF> points)
    {
        float l = float.MaxValue, b = float.MaxValue, r = float.MinValue, t = float.MinValue;
        bool any = false;
        foreach (var p in points)
        {
            any = true;
            l = Math.Min(l, p.X); r = Math.Max(r, p.X);
            b = Math.Min(b, p.Y); t = Math.Max(t, p.Y);
        }
        if (!any) throw new ArgumentException("점이 하나도 없습니다.", nameof(points));
        return new PdfRectF(l, b, r, t);
    }

    /// <summary>
    /// M1 규약(<c>RectangleF.FromLTRB(l, b, r, t)</c>)으로 담긴 사각형을 바꾼다.
    /// 그 규약에서는 r.Top이 아래 y, r.Bottom이 위 y다.
    /// </summary>
    public static PdfRectF FromLtrbRectangle(RectangleF r) => new(r.Left, r.Top, r.Right, r.Bottom);
}

/// <summary>
/// 네 점 사각형. P1 왼쪽 위, P2 오른쪽 위, P3 왼쪽 아래, P4 오른쪽 아래
/// (PDFium FS_QUADPOINTSF x1..x4, Acrobat 순서).
/// </summary>
public readonly record struct PdfQuadF(PdfPointF P1, PdfPointF P2, PdfPointF P3, PdfPointF P4)
{
    public static PdfQuadF FromRect(PdfRectF r)
        => new(new(r.Left, r.Top), new(r.Right, r.Top), new(r.Left, r.Bottom), new(r.Right, r.Bottom));

    public PdfRectF Bounds => PdfRectF.FromPoints([P1, P2, P3, P4]);
}
