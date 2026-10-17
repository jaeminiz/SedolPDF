using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

/// <summary>
/// 스탬프의 모양. 기본·글자 스탬프는 둥근 사각형 테두리(두께 2.5) + 가운데 정렬 한 줄 글자(Rect 높이의 45%, 넘치면 줄여 맞춤).
/// 그림 스탬프는 이미지 XObject 하나를 가로세로 비율을 지켜 Rect 가운데에 둔다.
/// 표시 문구는 호출한 쪽이 StampSpec.Text에 넣는다(Painter는 문구를 모른다). 기본 스탬프에 Text가 없으면 PDF 이름(BuiltinName)을 그린다.
/// </summary>
internal static class StampPainter
{
    public const float BorderWidth = 2.5f;
    public const float TextHeightRatio = 0.45f;
    private const float TextPadding = 4f;

    private static readonly PdfColor Green = PdfColor.FromHex("#1F9D55");
    private static readonly PdfColor Red = PdfColor.FromHex("#E5372D");
    private static readonly PdfColor Purple = PdfColor.FromHex("#8E44AD");
    private static readonly PdfColor Blue = PdfColor.FromHex("#005FB8");

    /// <summary>기본 6종의 고정 색. 모르는 이름은 null.</summary>
    public static PdfColor? BuiltinColor(string name) => name switch
    {
        "Approved" or "Reviewed" or "Done" => Green,
        "Rejected" => Red,
        "Confidential" => Purple,
        "Draft" => Blue,
        _ => null,
    };

    public static (ImmutableArray<TextRunOp> Texts, ImmutableArray<ImageOp> Images, PdfRectF? Bounds) Paint(
        Annotation a, PaintContext? ctx, List<PaintOp> ops)
    {
        var spec = a.Stamp ?? throw new InvalidOperationException("A Stamp annotation needs a StampSpec.");
        if (spec.Kind == StampKind.Image) return PaintImage(a, spec, ctx);

        var font = ctx?.Font ?? throw new InvalidOperationException("Painting a text stamp needs a PaintContext with the app font.");
        var color = spec.Kind == StampKind.Builtin
            ? BuiltinColor(spec.BuiltinName) ?? a.Color ?? Blue
            : a.Color ?? Blue;
        var r = a.Rect;
        if (r.Width <= 2 * BorderWidth || r.Height <= 2 * BorderWidth) return (ImmutableArray<TextRunOp>.Empty, ImmutableArray<ImageOp>.Empty, null);   // 너무 작아 그릴 수 없다

        // 둥근 사각형: 선 중심이 Rect 안쪽 BorderWidth/2에 오도록 줄인다.
        float h = BorderWidth / 2f;
        var inner = new PdfRectF(r.Left + h, r.Bottom + h, r.Right - h, r.Top - h);
        float radius = Math.Max(0f, Math.Min(Math.Min(inner.Width, inner.Height) / 2f, Math.Min(inner.Height * 0.2f, 12f)));
        ops.Add(new PaintOp(RoundedRect(inner, radius), null, color, BorderWidth, LineCap.Butt, LineJoin.Round));

        string text = (spec.Text ?? (spec.Kind == StampKind.Builtin ? spec.BuiltinName : "")).Replace("\r", " ").Replace("\n", " ");
        float availW = Math.Max(r.Width - 2 * (BorderWidth + TextPadding), 0f);
        float size = r.Height * TextHeightRatio;
        if (text.Length == 0 || availW <= 0f || size <= 0f) return (ImmutableArray<TextRunOp>.Empty, ImmutableArray<ImageOp>.Empty, null);

        // 한 줄: 먼저 실제 폭을 재고, 넘치면 같은 비율로 글자 크기를 줄인다.
        var probe = TextLayout.Layout(font, text, size, float.PositiveInfinity, TextAlign.Left, new PdfPointF(0, 0));
        float width = probe.Bounds.Width;
        if (width > availW && width > 0f) size *= availW / width * 0.999f;   // 반올림으로 1~2% 넘치지 않게 약간 더 줄인다
        float lineH = size * 1.2f;
        var topLeft = new PdfPointF(r.Left + BorderWidth + TextPadding, (r.Bottom + r.Top) / 2f + lineH / 2f);
        var laid = TextLayout.Layout(font, text, size, availW, TextAlign.Center, topLeft);
        var glyphs = laid.Lines.SelectMany(l => l).ToImmutableArray();
        if (glyphs.IsEmpty) return (ImmutableArray<TextRunOp>.Empty, ImmutableArray<ImageOp>.Empty, null);
        return ([new TextRunOp("F1", size, color, glyphs)], ImmutableArray<ImageOp>.Empty, laid.Bounds);
    }

    private static (ImmutableArray<TextRunOp>, ImmutableArray<ImageOp>, PdfRectF?) PaintImage(Annotation a, StampSpec spec, PaintContext? ctx)
    {
        string id = spec.ImageId ?? throw new InvalidOperationException("An image stamp needs an ImageId.");
        var r = a.Rect;
        var fit = r;
        if (ctx is not null && ctx.Assets.HasImage(id))
        {
            var (_, w, h, _) = ctx.Assets.GetImage(id);
            if (w > 0 && h > 0 && r.Width > 0 && r.Height > 0)
            {
                float scale = Math.Min(r.Width / w, r.Height / h);
                float fw = w * scale, fh = h * scale;
                float cx = (r.Left + r.Right) / 2f, cy = (r.Bottom + r.Top) / 2f;
                fit = new PdfRectF(cx - fw / 2f, cy - fh / 2f, cx + fw / 2f, cy + fh / 2f);
            }
        }
        return (ImmutableArray<TextRunOp>.Empty, [new ImageOp("Im1", id, fit)], fit);
    }

    private static ImmutableArray<PathSeg> RoundedRect(PdfRectF r, float rad)
    {
        float k = AnnotationPainter.Kappa * rad;
        PdfPointF P(float x, float y) => new(x, y);
        return
        [
            new MoveTo(P(r.Left + rad, r.Bottom)),
            new LineTo(P(r.Right - rad, r.Bottom)),
            new CubicTo(P(r.Right - rad + k, r.Bottom), P(r.Right, r.Bottom + rad - k), P(r.Right, r.Bottom + rad)),
            new LineTo(P(r.Right, r.Top - rad)),
            new CubicTo(P(r.Right, r.Top - rad + k), P(r.Right - rad + k, r.Top), P(r.Right - rad, r.Top)),
            new LineTo(P(r.Left + rad, r.Top)),
            new CubicTo(P(r.Left + rad - k, r.Top), P(r.Left, r.Top - rad + k), P(r.Left, r.Top - rad)),
            new LineTo(P(r.Left, r.Bottom + rad)),
            new CubicTo(P(r.Left, r.Bottom + rad - k), P(r.Left + rad - k, r.Bottom), P(r.Left + rad, r.Bottom)),
            new ClosePath(),
        ];
    }
}
