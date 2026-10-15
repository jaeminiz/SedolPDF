using System.Drawing;

namespace Pdf.Native;

public readonly record struct ViewPoint(double X, double Y);
public readonly record struct ViewRect(double X, double Y, double W, double H);
public readonly record struct ViewSize(double W, double H);

/// <summary>
/// 페이지 사용자 좌표(pt, 원점 왼쪽 아래, y 위쪽) ↔ 화면 픽셀(왼쪽 위 (0,0)) 변환. PDFium 호출 없음.
/// 회전 1 = 90도 시계 방향. boundsPt는 PdfPage.BoundsPt
/// (RectangleF.FromLTRB(l, b, r, t) 규약이므로 Top = 아래 y, Bottom = 위 y).
/// </summary>
public readonly struct PageTransform
{
    private readonly double _l, _t, _w, _h, _s;
    private readonly int _rot;

    public PageTransform(RectangleF boundsPt, int rotation, double scale)
    {
        _l = boundsPt.Left;
        _t = boundsPt.Bottom; // 위쪽 y
        _w = boundsPt.Width;
        _h = boundsPt.Height;
        _s = scale;
        _rot = ((rotation % 4) + 4) % 4;
    }

    public ViewSize ViewSize => _rot is 1 or 3 ? new ViewSize(_h * _s, _w * _s) : new ViewSize(_w * _s, _h * _s);

    public ViewPoint ToView(double xPt, double yPt)
    {
        double u = xPt - _l, v = _t - yPt;
        (double a, double b) = _rot switch
        {
            0 => (u, v),
            1 => (_h - v, u),
            2 => (_w - u, _h - v),
            _ => (v, _w - u),
        };
        return new ViewPoint(a * _s, b * _s);
    }

    public (double X, double Y) ToPage(ViewPoint view)
    {
        double a = view.X / _s, b = view.Y / _s;
        (double u, double v) = _rot switch
        {
            0 => (a, b),
            1 => (b, _h - a),
            2 => (_w - a, _h - b),
            _ => (_w - b, a),
        };
        return (_l + u, _t - v);
    }

    /// <summary>네 꼭짓점 변환 후 감싸는 사각형.</summary>
    public ViewRect ToView(RectangleF rectPt)
    {
        double l = rectPt.Left, r = rectPt.Right, b = rectPt.Top, t = rectPt.Bottom;
        var p = new[] { ToView(l, b), ToView(r, b), ToView(l, t), ToView(r, t) };
        double minX = p.Min(q => q.X), maxX = p.Max(q => q.X), minY = p.Min(q => q.Y), maxY = p.Max(q => q.Y);
        return new ViewRect(minX, minY, maxX - minX, maxY - minY);
    }
}
