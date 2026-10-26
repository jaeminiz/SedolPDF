using System.Collections.Immutable;
using System.Globalization;
using Pdf.Native;
using PdfEditor.Appearance;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Content;

namespace PdfEditor.TextEdit;

/// <summary>
/// 앱 글꼴(Noto Sans KR) 한 줄을 만든다(M4 C6). HarfBuzz 배치를 한 번 계산해 화면용 윤곽선(같은 글리프)과
/// 파일용 내용(같은 CID·위치)을 함께 만든다 — 두 출력이 같은 글리프·위치를 쓴다(설계서 4.2-3).
/// </summary>
public static class AppFontLine
{
    /// <summary>
    /// placement: 기준선 왼쪽 점을 원점으로 한 로컬 좌표 → 쪽 좌표(원래 줄의 텍스트 행렬, 글자 크기 제외).
    /// 글꼴에 없는 글자가 있으면 spec은 null이고 Missing에 코드포인트가 담긴다.
    /// </summary>
    public static (AppTextSpec? Spec, ImmutableArray<int> Missing) Build(AppFont font, string text, float sizePt, PdfMatrix placement, PdfColor color)
        => Build(font, text, sizePt, placement, color, AppFontFamily.Sans, false, false);

    /// <summary>카탈로그가 고른 글꼴(고딕/명조, 가짜 굵게·기울임)로 한 줄을 만든다(M5 C11).</summary>
    public static (AppTextSpec? Spec, ImmutableArray<int> Missing) Build(AppFontChoice choice, string text, float sizePt, PdfMatrix placement, PdfColor color)
        => Build(choice.Font, text, sizePt, placement, color, choice.Family, choice.SyntheticBold, choice.SyntheticItalic);

    /// <summary>
    /// 가짜 굵게: 렌더 모드 2(채우기+선)와 글자 크기 3 %의 선(선 색 = 글자 색). 가짜 기울임: 줄 기준선에서 기울임 행렬(x += 0.21·y).
    /// 화면 윤곽선은 기울임을 같은 식으로 반영하고, 굵게의 선 굵기는 <see cref="AppTextSpec.BoldStrokePt"/>로 알려 화면 그리기가 같은 굵기로 선을 긋게 한다.
    /// </summary>
    public static (AppTextSpec? Spec, ImmutableArray<int> Missing) Build(AppFont font, string text, float sizePt, PdfMatrix placement, PdfColor color,
        AppFontFamily family, bool syntheticBold, bool syntheticItalic)
    {
        string line = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        var laid = TextLayout.Layout(font, line, sizePt, float.PositiveInfinity, TextAlign.Left, new PdfPointF(0, 0));
        if (!laid.MissingCodepoints.IsDefaultOrEmpty) return (null, laid.MissingCodepoints);
        var glyphs = laid.Lines.IsDefaultOrEmpty ? ImmutableArray<PositionedGlyph>.Empty : laid.Lines[0];
        if (glyphs.IsEmpty) return (null, []);

        // 기준선을 y = 0으로
        float baseY = glyphs[0].Origin.Y;
        var local = glyphs.Select(g => g with { Origin = new PdfPointF(g.Origin.X, g.Origin.Y - baseY) }).ToImmutableArray();

        // 파일용 내용: 로컬 좌표로 쓴 BT…ET를 placement cm으로 감싼다. 글꼴 자원 이름은 자리표시 {F}
        var run = new TextRunOp("{F}", sizePt, color, local);
        var drawing = new AppearanceDrawing([], 1f, default) { Texts = [run] };
        float skew = syntheticItalic ? AppFontCatalog.SyntheticItalicSkew : 0f;
        float stroke = syntheticBold ? sizePt * AppFontCatalog.SyntheticBoldStrokeRatio : 0f;
        string extra = (skew != 0 ? "1 0 " + Num(skew) + " 1 0 0 cm\n" : "")
            + (stroke > 0 ? Num(color.R / 255f) + " " + Num(color.G / 255f) + " " + Num(color.B / 255f) + " RG\n" + Num(stroke) + " w\n2 Tr\n" : "");
        string content = "q\n" + Matrix(placement) + " cm\n" + extra + PdfContentWriter.Write(drawing) + "Q\n";

        // 화면용 윤곽선(쪽 좌표)
        var outline = ImmutableArray.CreateBuilder<OutlineSeg>();
        foreach (var g in local)
        {
            foreach (var seg in GlyphOutlines.Get(font, g.Gid))
            {
                PdfPointF P(PdfPointF em) => placement.Apply(new PdfPointF(g.Origin.X + em.X * sizePt + skew * em.Y * sizePt, g.Origin.Y + em.Y * sizePt));
                switch (seg)
                {
                    case MoveTo m: outline.Add(new OutlineSeg('M', P(m.P))); break;
                    case LineTo l: outline.Add(new OutlineSeg('L', P(l.P))); break;
                    case CubicTo c: outline.Add(new OutlineSeg('C', P(c.C1), P(c.C2), P(c.P))); break;
                    case ClosePath: outline.Add(new OutlineSeg('Z', default)); break;
                }
            }
        }

        float width = local[^1].Origin.X + local[^1].Advance;
        float asc = font.Ascender1000 * sizePt / 1000f, desc = font.Descender1000 * sizePt / 1000f;
        var bounds = PdfRectF.FromPoints([
            placement.Apply(new(0, desc)), placement.Apply(new(width, desc)),
            placement.Apply(new(0, asc)), placement.Apply(new(width, asc))]);

        var use = drawing.CollectFontUse() with { Family = family == AppFontFamily.Serif ? FontUse.SerifFamily : "" };
        return (new AppTextSpec(line, sizePt, color, placement, bounds, outline.ToImmutable(), content, use, stroke), []);
    }

    private static string Num(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Matrix(PdfMatrix m) =>
        string.Join(' ', new[] { m.A, m.B, m.C, m.D, m.E, m.F }.Select(v => v.ToString("0.#####", CultureInfo.InvariantCulture)));
}
