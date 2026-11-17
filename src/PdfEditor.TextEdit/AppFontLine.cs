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
/// <summary>설치된 글꼴로 줄을 쓸 때(0.12.0): 글꼴 키, 보이는 이름, 진짜 굵은·기울인 얼굴인가.</summary>
public sealed record SystemFontPick(string Key, string Name, bool RealBold, bool RealItalic);

public static class AppFontLine
{
    /// <summary>
    /// placement: 기준선 왼쪽 점을 원점으로 한 로컬 좌표 → 쪽 좌표(원래 줄의 텍스트 행렬, 글자 크기 제외).
    /// 글꼴에 없는 글자가 있으면 spec은 null이고 Missing에 코드포인트가 담긴다.
    /// </summary>
    public static (AppTextSpec? Spec, ImmutableArray<int> Missing) Build(AppFont font, string text, float sizePt, PdfMatrix placement, PdfColor color)
        => Build(font, text, sizePt, placement, color, AppFontFamily.Sans, false, false);

    /// <summary>카탈로그가 고른 글꼴(고딕/명조, 가짜 굵게·기울임)로 한 줄을 만든다(M5 C11).</summary>
    public static (AppTextSpec? Spec, ImmutableArray<int> Missing) Build(AppFontChoice choice, string text, float sizePt, PdfMatrix placement, PdfColor color, float? boldStrokeRatio = null)
        => Build(choice.Font, text, sizePt, placement, color, choice.Family, choice.SyntheticBold, choice.SyntheticItalic, boldStrokeRatio);

    /// <summary>
    /// 가짜 굵게: 렌더 모드 2(채우기+선)와 글자 크기 3 %의 선(선 색 = 글자 색). 가짜 기울임: 줄 기준선에서 기울임 행렬(x += 0.21·y).
    /// 화면 윤곽선은 기울임을 같은 식으로 반영하고, 굵게의 선 굵기는 <see cref="AppTextSpec.BoldStrokePt"/>로 알려 화면 그리기가 같은 굵기로 선을 긋게 한다.
    /// </summary>
    public static (AppTextSpec? Spec, ImmutableArray<int> Missing) Build(AppFont font, string text, float sizePt, PdfMatrix placement, PdfColor color,
        AppFontFamily family, bool syntheticBold, bool syntheticItalic, float? boldStrokeRatio = null, SystemFontPick? system = null, AppFontChoice? fallback = null)
    {
        string line = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        // 글자별 글꼴 대체(0.13.0): fallback이 있으면 font에 없는 글자만 그 글꼴(Noto)로 쓴다. 없으면 지금과 같은 한 글꼴.
        IReadOnlyList<AppFont> fonts = fallback is null ? [font] : [font, fallback.Font];
        var laid = TextLayout.Layout(fonts, line, sizePt, float.PositiveInfinity, TextAlign.Left, new PdfPointF(0, 0));
        if (!laid.MissingCodepoints.IsDefaultOrEmpty) return (null, laid.MissingCodepoints);
        var glyphs = laid.Lines.IsDefaultOrEmpty ? ImmutableArray<PositionedGlyph>.Empty : laid.Lines[0];
        if (glyphs.IsEmpty) return (null, []);

        // 기준선을 y = 0으로
        float baseY = glyphs[0].Origin.Y;
        var local = glyphs.Select(g => g with { Origin = new PdfPointF(g.Origin.X, g.Origin.Y - baseY) }).ToImmutableArray();

        // 글꼴 구간마다 런 하나: 고른 글꼴 {F}, 대체 글꼴 {F2}. 기울임은 구간마다(진짜 기울인 얼굴은 0), 굵게의 선은 한 줄 전체
        // (화면 경로 객체가 선 굵기를 하나만 가지므로 한 구간이라도 가짜 굵게면 줄 전체에 같은 선을 긋는다 — 화면과 저장이 같다).
        bool[] runItalic = [syntheticItalic, fallback?.SyntheticItalic ?? false];
        float stroke = syntheticBold || fallback?.SyntheticBold == true ? sizePt * (boldStrokeRatio ?? AppFontCatalog.SyntheticBoldStrokeRatio) : 0f;
        var runs = ImmutableArray.CreateBuilder<TextRunOp>();
        string content = "";
        for (int i = 0; i < local.Length;)
        {
            int fi = local[i].FontIndex, j = i + 1;
            while (j < local.Length && local[j].FontIndex == fi) j++;
            var run = new TextRunOp(fi == 0 ? "{F}" : "{F2}", sizePt, color, ImmutableArray.CreateRange(local.Skip(i).Take(j - i)));
            runs.Add(run);
            // 파일용 내용: 로컬 좌표로 쓴 BT…ET를 placement cm으로 감싼다(구간마다 q … Q).
            float runSkew = runItalic[fi] ? AppFontCatalog.SyntheticItalicSkew : 0f;
            string extraRun = (runSkew != 0 ? "1 0 " + Num(runSkew) + " 1 0 0 cm\n" : "")
                + (stroke > 0 ? Num(color.R / 255f) + " " + Num(color.G / 255f) + " " + Num(color.B / 255f) + " RG\n" + Num(stroke) + " w\n2 Tr\n" : "");
            content += "q\n" + Matrix(placement) + " cm\n" + extraRun + PdfContentWriter.Write(new AppearanceDrawing([], 1f, default) { Texts = [run] }) + "Q\n";
            i = j;
        }
        var drawing = new AppearanceDrawing([], 1f, default) { Texts = runs.ToImmutable() };

        // 화면용 윤곽선(쪽 좌표)
        var outline = ImmutableArray.CreateBuilder<OutlineSeg>();
        foreach (var g in local)
        {
            var gfont = fonts[g.FontIndex];
            float gskew = runItalic[g.FontIndex] ? AppFontCatalog.SyntheticItalicSkew : 0f;
            foreach (var seg in GlyphOutlines.Get(gfont, g.Gid))
            {
                PdfPointF P(PdfPointF em) => placement.Apply(new PdfPointF(g.Origin.X + em.X * sizePt + gskew * em.Y * sizePt, g.Origin.Y + em.Y * sizePt));
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
        float asc = font.Ascender1000 * sizePt / 1000f, desc = font.Descender1000 * sizePt / 1000f;   // 줄 높이는 고른 글꼴 기준
        var bounds = PdfRectF.FromPoints([
            placement.Apply(new(0, desc)), placement.Apply(new(width, desc)),
            placement.Apply(new(0, asc)), placement.Apply(new(width, asc))]);

        // 설치된 글꼴이면 글꼴 키(sys:경로#번호)로: 저장 때 패처가 같은 파일의 서브셋을 넣는다.
        var uses = drawing.CollectFontUses();
        var use = (uses.Any(u => u.Resource == "{F}") ? uses.First(u => u.Resource == "{F}").Use : new FontUse([], [])) with { Family = system?.Key ?? (family == AppFontFamily.Serif ? FontUse.SerifFamily : "") };
        FontUse? fallbackUse = fallback is null || uses.All(u => u.Resource != "{F2}") ? null
            : uses.First(u => u.Resource == "{F2}").Use with { Family = fallback.Family == AppFontFamily.Serif ? FontUse.SerifFamily : "" };
        return (new AppTextSpec(line, sizePt, color, placement, bounds, outline.ToImmutable(), content, use, stroke, syntheticItalic, family == AppFontFamily.Serif,
            system?.Name, system?.RealBold ?? false, system?.RealItalic ?? false) { FallbackFont = fallbackUse }, []);
    }

    private static string Num(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Matrix(PdfMatrix m) =>
        string.Join(' ', new[] { m.A, m.B, m.C, m.D, m.E, m.F }.Select(v => v.ToString("0.#####", CultureInfo.InvariantCulture)));
}
