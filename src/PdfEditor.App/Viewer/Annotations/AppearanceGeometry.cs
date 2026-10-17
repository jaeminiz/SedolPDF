using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Pdf.Native;
using PdfEditor.Appearance;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;
using AvLineCap = Avalonia.Media.PenLineCap;
using AvLineJoin = Avalonia.Media.PenLineJoin;

namespace PdfEditor.App.Viewer.Annotations;

/// <summary>한 주석의 화면용 그리기 항목 목록. 경로는 쪽 pt 좌표 그대로(변환 행렬이 화면으로 옮긴다).</summary>
public sealed record CachedAppearance(IReadOnlyList<(Geometry Geometry, IBrush? Fill, IPen? Stroke)> Items)
{
    /// <summary>그림(Painter의 ImageOp). 경로 다음, 글자 앞에 그린다. Bitmap이 null이면 건너뛴다.</summary>
    public IReadOnlyList<(PdfRectF Rect, IImage? Bitmap)> Images { get; init; } = [];

    /// <summary>글자: 글리프 윤곽선으로 채운 경로(PDF 글자와 같은 글리프·위치). 그림 다음에 그린다.</summary>
    public IReadOnlyList<(Geometry Geometry, IBrush? Fill, IPen? Stroke)> Texts { get; init; } = [];

    /// <summary>경로 → 그림 → 글자 순(PDF 내용 스트림 순서와 같다).</summary>
    public void Draw(DrawingContext ctx, double opacity)
    {
        foreach (var (geo, fill, stroke) in Items) ctx.DrawGeometry(fill, stroke, geo);
        foreach (var (rect, bmp) in Images)
        {
            if (bmp is null) continue;
            // 쪽 좌표는 y가 위쪽이고 바깥 변환이 화면용으로 뒤집으므로, 그림만 한 번 더 뒤집어 바로 세운다.
            using (ctx.PushTransform(new Matrix(1, 0, 0, -1, 0, rect.Bottom + rect.Top)))
            using (ctx.PushOpacity(opacity))
                ctx.DrawImage(bmp, new Rect(0, 0, bmp.Size.Width, bmp.Size.Height),
                    new Rect(rect.Left, rect.Bottom, rect.Width, rect.Height));
        }
        foreach (var (geo, fill, stroke) in Texts) ctx.DrawGeometry(fill, stroke, geo);
    }
}

/// <summary>
/// <see cref="AnnotationPainter"/>의 <see cref="AppearanceDrawing"/>을 Avalonia 도형으로 바꾼다. 모양 계산은 하지 않는다.
/// 주석 인스턴스(변경하면 새 인스턴스) × 쪽 회전마다 한 번만 만든다. UI 스레드에서 쓴다.
/// </summary>
public static class AppearanceGeometry
{
    /// <summary>글자 윤곽선을 가져올 앱 글꼴(Noto Sans KR). 앱 시작 때 한 번 정한다. 없으면 글자는 그리지 않는다.</summary>
    public static AppFont? TextFont { get; set; }

    /// <summary>ImageOp.ImageId에 해당하는 비트맵을 돌려준다(AnnotationAssets). 없으면 그림은 건너뛴다.</summary>
    public static Func<string, IImage?>? ImageResolver { get; set; }

    private static readonly ConditionalWeakTable<Annotation, Dictionary<int, CachedAppearance>> Cache = new();

    public static CachedAppearance Get(Annotation a, int pageRotation)
    {
        int rot = ((pageRotation % 4) + 4) % 4;
        var perRot = Cache.GetValue(a, _ => new Dictionary<int, CachedAppearance>());
        lock (perRot)
        {
            if (!perRot.TryGetValue(rot, out var c))
                perRot[rot] = c = FromDrawing(AnnotationPainter.Paint(a, rot));
            return c;
        }
    }

    public static CachedAppearance FromDrawing(AppearanceDrawing d)
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
        var images = new List<(PdfRectF, IImage?)>(d.Images.Length);
        foreach (var im in d.Images) images.Add((im.Rect, ImageResolver?.Invoke(im.ImageId)));

        var texts = new List<(Geometry, IBrush?, IPen?)>(d.Texts.Length);
        foreach (var run in d.Texts)
        {
            var font = TextFont;
            if (font is null || run.Glyphs.IsEmpty) continue;
            var path = ImmutableArray.CreateBuilder<PathSeg>();
            foreach (var g in run.Glyphs)
                foreach (var seg in GlyphOutlines.Get(font, g.Gid))
                    path.Add(Place(seg, run.SizePt, g.Origin));
            var brush = new ImmutableSolidColorBrush(Color.FromArgb(alpha, run.Color.R, run.Color.G, run.Color.B));
            var op = new PaintOp(path.ToImmutable(), run.Color, null, 0, PdfEditor.Appearance.LineCap.Butt, PdfEditor.Appearance.LineJoin.Miter);
            texts.Add((ToGeometry(op), brush, null));
        }
        return new CachedAppearance(items) { Images = images, Texts = texts };
    }

    /// <summary>글리프 윤곽선(em 단위)을 글자 크기만큼 키우고 기준선 원점으로 옮긴다.</summary>
    private static PathSeg Place(PathSeg s, float size, PdfPointF o)
    {
        PdfPointF P(PdfPointF p) => new(o.X + p.X * size, o.Y + p.Y * size);
        return s switch
        {
            MoveTo m => new MoveTo(P(m.P)),
            LineTo l => new LineTo(P(l.P)),
            CubicTo c => new CubicTo(P(c.C1), P(c.C2), P(c.P)),
            _ => s,
        };
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
