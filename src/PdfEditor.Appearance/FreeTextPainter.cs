using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

/// <summary>
/// 텍스트 상자·타자기·설명선(FreeText)의 모양(A6). 글자는 TextLayout이 정한 글리프를 그대로 쓴다.
/// 상자 안쪽 여백 2pt, 줄 간격 1.2 x 글자 크기. 투명도는 지원하지 않는다(AP에 /GS를 못 넣는 패치 길).
/// </summary>
internal static class FreeTextPainter
{
    public const float Padding = 2f;
    private static readonly PdfColor Black = new(0, 0, 0);

    public static FreeTextIntent IntentOf(Annotation a) => a.Intent ?? FreeTextIntent.TextBox;

    /// <summary>글 상자: 설명선이면 TextBox, 아니면 Rect.</summary>
    public static PdfRectF BoxOf(Annotation a) => IntentOf(a) == FreeTextIntent.Callout ? a.TextBox ?? a.Rect : a.Rect;

    /// <summary>타자기: 왼쪽 위를 고정하고 상자를 글 크기에 맞춘다(자동 줄바꿈 없음). 나머지 유형은 그대로 돌려준다.</summary>
    public static Annotation FitTypewriter(Annotation page, PaintContext ctx)
    {
        if (IntentOf(page) != FreeTextIntent.Typewriter) return page;
        // 회전한 타자기는 바로 선 자기 좌표에서 맞춘 뒤 되돌린다(화면에서 본 왼쪽 위가 고정된다).
        var a = AnnotationRotation.ToLocal(page, out var toPage);
        var laid = TextLayout.Layout(ctx.Font ?? throw new InvalidOperationException("Fitting a typewriter annotation needs the app font."), a.Contents ?? "", a.FontSizePt, float.PositiveInfinity, a.Align,
            new PdfPointF(a.Rect.Left + Padding, a.Rect.Top - Padding));
        float lineH = a.FontSizePt * 1.2f;
        float w = Math.Max(laid.Bounds.Width, 0f) + 2 * Padding;
        float h = Math.Max(laid.Lines.Length, 1) * lineH + 2 * Padding;
        return AnnotationRotation.FromLocal(a with { Rect = new PdfRectF(a.Rect.Left, a.Rect.Top - h, a.Rect.Left + w, a.Rect.Top) }, toPage, page.Rotation);
    }

    public static (ImmutableArray<TextRunOp> Texts, PdfRectF? TextBounds) Paint(
        Annotation a, PaintContext? ctx, List<PaintOp> ops)
    {
        var font = ctx?.Font ?? throw new InvalidOperationException("Painting a FreeText annotation needs a PaintContext with the app font.");
        var intent = IntentOf(a);
        var box = BoxOf(a);

        if (intent != FreeTextIntent.Typewriter)
        {
            var stroke = a.BorderWidth > 0 ? a.Color : null;
            if (a.FillColor is not null || stroke is not null)
            {
                float h = stroke is null ? 0f : a.BorderWidth / 2f;
                var r = new PdfRectF(box.Left + h, box.Bottom + h, box.Right - h, box.Top - h);
                ImmutableArray<PathSeg> path =
                [
                    new MoveTo(new(r.Left, r.Bottom)), new LineTo(new(r.Right, r.Bottom)),
                    new LineTo(new(r.Right, r.Top)), new LineTo(new(r.Left, r.Top)), new ClosePath(),
                ];
                ops.Add(new PaintOp(path, a.FillColor, stroke, stroke is null ? 0f : a.BorderWidth, LineCap.Butt, LineJoin.Miter));
            }
        }

        if (intent == FreeTextIntent.Callout && !a.Vertices.IsDefaultOrEmpty && a.Vertices.Length >= 2)
        {
            // 지시선: 끝 모양은 화살표가 가리키는 첫 점(Vertices[0])에 붙는다.
            var leader = a with
            {
                Kind = AnnotKind.PolyLine, Color = a.Color ?? Black, FillColor = null,
                BorderWidth = a.BorderWidth > 0 ? a.BorderWidth : 1f,
                StartEnding = a.EndEnding, EndEnding = LineEnding.None,
            };
            AnnotationPainter.AddPolyline(leader, ops);
        }

        var topLeft = new PdfPointF(box.Left + Padding, box.Top - Padding);
        float width = intent == FreeTextIntent.Typewriter ? float.PositiveInfinity : Math.Max(box.Width - 2 * Padding, 0f);
        var laid = TextLayout.Layout(font, a.Contents ?? "", a.FontSizePt, width, a.Align, topLeft);
        var glyphs = laid.Lines.SelectMany(l => l).ToImmutableArray();
        if (glyphs.IsEmpty) return (ImmutableArray<TextRunOp>.Empty, null);
        return ([new TextRunOp("F1", a.FontSizePt, a.TextColor ?? Black, glyphs)], laid.Bounds);
    }
}
