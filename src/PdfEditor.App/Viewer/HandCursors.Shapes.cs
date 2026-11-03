using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PdfEditor.App.Viewer;

/// <summary>손 커서 모양 (0.5.0): 32×32 도안을 손가락·손바닥·엄지 둥근 막대의 합집합으로 만들어 안티앨리어싱으로 그린다.</summary>
public enum HandCursorStyle
{
    /// <summary>시안 A — 흰 손 + 검은 윤곽 + 옅은 그림자 (밝은·어두운 바탕 모두에서 잘 보임). 기본.</summary>
    Classic,
    /// <summary>시안 B — 검은 손 + 흰 윤곽 (밝은 문서 위에서 가장 또렷함).</summary>
    Dark,
    /// <summary>시안 C — 흰 손 + 남색 윤곽 + 손가락 주름 (앱 색에 맞춘 부드러운 인상).</summary>
    Navy,
}

public static partial class HandCursors
{
    /// <summary>사용할 모양. 사용자가 시안을 고르면 이 값만 바꾼다.</summary>
    public static HandCursorStyle DefaultStyle { get; } = HandCursorStyle.Classic;

    private static RectangleGeometry Capsule(double x, double y, double w, double h) => new(new Rect(x, y, w, h)) { RadiusX = w / 2, RadiusY = w / 2 };

    private static Geometry Union(params Geometry[] parts)
    {
        var g = parts[0];
        for (int i = 1; i < parts.Length; i++) g = new CombinedGeometry(GeometryCombineMode.Union, g, parts[i]);
        return g;
    }

    private static RectangleGeometry Tilted(double cx, double cy, double w, double h, double degrees)
        => new(new Rect(-w / 2, -h / 2, w, h)) { RadiusX = w / 2, RadiusY = w / 2, Transform = new MatrixTransform(Matrix.CreateRotation(degrees * Math.PI / 180) * Matrix.CreateTranslation(cx, cy)) };

    /// <summary>펼친 손(대기): 손가락 4개 + 엄지 + 손바닥. 좌표는 32×32.</summary>
    public static Geometry OpenShape() => Union(
        Capsule(9.0, 6.0, 3.6, 16), Capsule(13.1, 3.0, 3.6, 19), Capsule(17.2, 5.0, 3.6, 17), Capsule(21.3, 8.5, 3.5, 13.5),
        new RectangleGeometry(new Rect(9.0, 15.0, 15.8, 12.5)) { RadiusX = 6, RadiusY = 6 },
        Tilted(6.9, 19.6, 3.9, 11.5, -34));

    /// <summary>쥔 손(끄는 중): 손가락 마디 4개가 위로 둥글게 나오고 엄지가 가로로 덮는다.</summary>
    public static Geometry GrabShape() => Union(
        Capsule(8.6, 12.2, 3.8, 9), Capsule(12.8, 10.8, 3.8, 10), Capsule(17.0, 11.2, 3.8, 10), Capsule(21.1, 12.8, 3.7, 9),
        new RectangleGeometry(new Rect(8.6, 15.0, 16.2, 12.5)) { RadiusX = 6, RadiusY = 6 },
        Tilted(12.6, 22.3, 4.0, 11.0, 90 - 8));

    /// <summary>비트맵 한 장으로 그린다(투명 바탕).</summary>
    public static RenderTargetBitmap Render(bool grab, HandCursorStyle style, int px = SizePx)
    {
        var geo = grab ? GrabShape() : OpenShape();
        var (fill, line, width, shadow, crease) = style switch
        {
            HandCursorStyle.Dark => (Color.FromRgb(0x1C, 0x1C, 0x1E), Colors.White, 1.1, Color.FromArgb(0x40, 0, 0, 0), Color.FromRgb(0x6A, 0x6A, 0x70)),
            HandCursorStyle.Navy => (Colors.White, Color.FromRgb(0x0B, 0x2F, 0x5B), 1.35, Color.FromArgb(0x38, 0, 0x10, 0x30), Color.FromRgb(0x7F, 0x95, 0xB3)),
            _ => (Colors.White, Colors.Black, 1.1, Color.FromArgb(0x38, 0, 0, 0), Color.FromRgb(0x9A, 0x9A, 0x9A)),
        };
        var bmp = new RenderTargetBitmap(new PixelSize(px, px), new Vector(96, 96));
        using var ctx = bmp.CreateDrawingContext();
        using var _ = ctx.PushTransform(Matrix.CreateScale(px / 32.0, px / 32.0));
        using (ctx.PushTransform(Matrix.CreateTranslation(1.0, 1.3)))
            ctx.DrawGeometry(new SolidColorBrush(shadow), new Pen(new SolidColorBrush(shadow), width, lineJoin: PenLineJoin.Round), geo);
        ctx.DrawGeometry(new SolidColorBrush(fill), new Pen(new SolidColorBrush(line), width, lineJoin: PenLineJoin.Round), geo);
        if (style == HandCursorStyle.Navy)
        {
            // 손가락 주름: 펼친 손은 손바닥 위쪽 가로줄 하나, 쥔 손은 마디 아래 가로줄
            var pen = new Pen(new SolidColorBrush(crease), 0.7, lineCap: PenLineCap.Round);
            if (grab) ctx.DrawLine(pen, new Point(10.5, 18.6), new Point(22.5, 18.6));
            else ctx.DrawLine(pen, new Point(12, 21.6), new Point(21.8, 21.6));
        }
        return bmp;
    }
}
