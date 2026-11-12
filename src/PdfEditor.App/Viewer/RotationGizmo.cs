using Avalonia;
using Avalonia.Media;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 회전 손잡이와 돌리는 중 보이는 부채꼴·각도 라벨을 그린다. 객체 회전(DocumentView.Objects)과 주석 회전(DocumentView.Annotate)이 같이 쓴다(0.10.0에서 객체 쪽 코드를 옮겼다 — 모양은 그대로).
/// </summary>
internal static class RotationGizmo
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#005FB8"));

    /// <summary>경계 위쪽 가운데(edge)에서 handle까지 뻗은 선과 끝의 동그라미.</summary>
    public static void DrawHandle(DrawingContext ctx, Point edge, Point handle)
    {
        ctx.DrawLine(new Pen(Accent, 1), edge, handle);
        ctx.DrawEllipse(Brushes.White, new Pen(Accent, 1.5), handle, 5, 5);
    }

    /// <summary>
    /// 돌리는 중: 중심 표시, 처음 방향(a0)에서 지금 방향(a0 + sweep)까지의 부채꼴, 각도 라벨.
    /// onArc는 쪽 좌표 각도 → 반지름 radius 원 위의 화면 점(쪽 회전을 반영하는 일은 부르는 쪽이 한다).
    /// </summary>
    public static void DrawSweep(DrawingContext ctx, Point center, double radius, Func<float, Point> onArc, float a0, float sweep, string label)
    {
        var pen = new Pen(Accent, 1, new DashStyle([4, 3], 0));
        if (Math.Abs(sweep) > 0.002f)
        {
            var fan = new StreamGeometry();
            using (var gc = fan.Open())
            {
                gc.BeginFigure(center, true);
                int n = Math.Max(2, (int)(Math.Abs(sweep) / (Math.PI / 36)) + 1);
                for (int i = 0; i <= n; i++) gc.LineTo(onArc(a0 + sweep * i / n));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x5F, 0xB8)), pen, fan);
        }
        ctx.DrawEllipse(Brushes.White, new Pen(Accent, 1.5), center, 5, 5);
        var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.Black);
        var lp = onArc(a0 + sweep);
        var lbox = new Rect(lp.X + 8, lp.Y - text.Height / 2 - 3, text.Width + 10, text.Height + 6);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xC8)), new Pen(Accent, 1), lbox, 3, 3);
        ctx.DrawText(text, new Point(lbox.X + 5, lbox.Y + 3));
    }

    /// <summary>쪽 좌표 각도 → 중심에서 radius만큼 떨어진 화면 점. toView는 쪽 좌표 → 화면 점.</summary>
    public static Func<float, Point> ArcPoint(Func<float, float, Point> toView, float cx, float cy, Point center, double radius) => ang =>
    {
        var v = toView(cx + (float)Math.Cos(ang) * 1000f, cy + (float)Math.Sin(ang) * 1000f);
        double dx = v.X - center.X, dy = v.Y - center.Y, len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-6 ? center : new Point(center.X + dx / len * radius, center.Y + dy / len * radius);
    };

    /// <summary>끄는 각(라디안) → "37.4°" (0~360, 소수 한 자리).</summary>
    public static string AngleLabel(float sweep)
    {
        double deg = Math.Round(((sweep * 180 / Math.PI) % 360 + 360) % 360, 1) % 360;
        return deg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "°";
    }
}
