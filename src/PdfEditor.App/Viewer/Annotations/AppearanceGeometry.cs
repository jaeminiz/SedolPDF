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

    /// <summary>그림에 곱하는 투명도(경로·글자는 만들 때 알파가 색에 들어 있다).</summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>모든 입력(그림 비트맵)이 갖춰진 결과인가. false면 캐시하지 않는다(나중에 다시 만들어야 한다).</summary>
    public bool Complete { get; init; } = true;

    /// <summary>경로 → 그림 → 글자 순(PDF 내용 스트림 순서와 같다).</summary>
    public void Draw(DrawingContext ctx)
    {
        foreach (var (geo, fill, stroke) in Items) ctx.DrawGeometry(fill, stroke, geo);
        foreach (var (rect, bmp) in Images)
        {
            if (bmp is null) continue;
            // 쪽 좌표는 y가 위쪽이고 바깥 변환이 화면용으로 뒤집으므로, 그림만 한 번 더 뒤집어 바로 세운다.
            using (ctx.PushTransform(new Matrix(1, 0, 0, -1, 0, rect.Bottom + rect.Top)))
            using (ctx.PushOpacity(Opacity))
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
    /// <summary>글자 윤곽선을 가져올 앱 글꼴(Noto Sans KR). 앱 시작 때 한 번 정한다. 글자가 있는데 없으면 FromDrawing이 예외를 던진다.</summary>
    public static AppFont? TextFont
    {
        get => _textFont ?? _textFontProvider?.Invoke();
        set { _textFont = value; _textFontProvider = null; }   // 직접 정하면 공급자는 버린다(시험이 null로 되돌릴 수 있게)
    }

    private static AppFont? _textFont;
    private static Func<AppFont?>? _textFontProvider;

    /// <summary>글꼴을 글자가 있는 주석을 처음 그릴 때 공급자에게서 받는다(시작 때 읽지 않는다). TextFont를 직접 정하면 이 공급자는 지워진다.</summary>
    public static void SetTextFontProvider(Func<AppFont?> provider) { _textFont = null; _textFontProvider = provider; }

    /// <summary>ImageOp.ImageId에 해당하는 비트맵을 돌려준다(AnnotationAssets). 없거나 null을 돌려주면 자리 표시 사각형을 그리고 캐시하지 않는다.</summary>
    public static Func<string, IImage?>? ImageResolver { get; set; }

    /// <summary>그림 스탬프의 가로세로 비율을 알기 위한 보관소(PDF AP를 만들 때와 같은 것). 앱 시작 때 정한다.</summary>
    public static AnnotationAssets Assets { get; set; } = AnnotationAssets.Empty;

    private static readonly ConditionalWeakTable<Annotation, Dictionary<int, CachedAppearance>> Cache = new();

    public static CachedAppearance Get(Annotation a, int pageRotation)
    {
        int rot = ((pageRotation % 4) + 4) % 4;
        var perRot = Cache.GetValue(a, _ => new Dictionary<int, CachedAppearance>());
        lock (perRot)
        {
            if (perRot.TryGetValue(rot, out var c)) return c;
            c = FromDrawing(AnnotationPainter.Paint(a, rot, PaintContext.WithFontProvider(() => TextFont, Assets)));
            if (c.Complete) perRot[rot] = c;   // 그림이 아직 없으면 캐시하지 않아 다음에 다시 만든다
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
        bool complete = true;
        var images = new List<(PdfRectF, IImage?)>(d.Images.Length);
        foreach (var im in d.Images)
        {
            var resolver = ImageResolver ?? throw new InvalidOperationException(
                "AppearanceGeometry.ImageResolver is not set; image ops cannot be drawn.");
            var bmp = resolver(im.ImageId);
            if (bmp is null)
            {
                // 보이는 자리 표시: 그림 칸에 X 모양 없이 얇은 테두리 사각형만 그린다(캐시하지 않음).
                complete = false;
                var r = im.Rect;
                var box = new PaintOp([new MoveTo(new PdfPointF(r.Left, r.Bottom)), new LineTo(new PdfPointF(r.Right, r.Bottom)),
                    new LineTo(new PdfPointF(r.Right, r.Top)), new LineTo(new PdfPointF(r.Left, r.Top)), new ClosePath()],
                    null, new PdfColor(160, 160, 160), 1f, PdfEditor.Appearance.LineCap.Butt, PdfEditor.Appearance.LineJoin.Miter);
                items.Add((ToGeometry(box), null,
                    new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(alpha, 160, 160, 160)), 1)));
            }
            images.Add((im.Rect, bmp));
        }

        var texts = new List<(Geometry, IBrush?, IPen?)>(d.Texts.Length);
        foreach (var run in d.Texts)
        {
            if (run.Glyphs.IsEmpty) continue;
            var font = TextFont ?? throw new InvalidOperationException(
                "AppearanceGeometry.TextFont is not set; text runs cannot be drawn.");
            var path = ImmutableArray.CreateBuilder<PathSeg>();
            foreach (var g in run.Glyphs)
                foreach (var seg in GlyphOutlines.Get(font, g.Gid))
                    path.Add(Place(seg, run.SizePt, g.Origin));
            var brush = new ImmutableSolidColorBrush(Color.FromArgb(alpha, run.Color.R, run.Color.G, run.Color.B));
            var op = new PaintOp(path.ToImmutable(), run.Color, null, 0, PdfEditor.Appearance.LineCap.Butt, PdfEditor.Appearance.LineJoin.Miter);
            texts.Add((ToGeometry(op), brush, null));
        }
        return new CachedAppearance(items) { Images = images, Texts = texts, Opacity = d.Opacity, Complete = complete };
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
