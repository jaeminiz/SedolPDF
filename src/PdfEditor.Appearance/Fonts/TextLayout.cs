using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance.Fonts;

/// <summary>
/// 배치된 글리프 하나. <see cref="Origin"/>은 기준선 위의 왼쪽 점(PDF 사용자 좌표, 커닝 반영).
/// <see cref="Advance"/>는 글꼴이 정한 글리프 폭(pt, 커닝 제외)이라 PDF /W에 그대로 쓴다.
/// <see cref="Text"/>는 이 글리프가 처음 나타내는 원문(합자의 둘째 글리프 등은 빈 문자열).
/// </summary>
public sealed record PositionedGlyph(int Gid, int Cid, PdfPointF Origin, float Advance, string Text)
{
    /// <summary>글자별 글꼴 대체(0.13.0): 이 글리프가 속한 글꼴이 TextLayout.Layout에 준 목록의 몇 번째인가. 글꼴이 하나면 항상 0.</summary>
    public int FontIndex { get; init; }
}

public sealed record LaidOutText(
    ImmutableArray<ImmutableArray<PositionedGlyph>> Lines, PdfRectF Bounds, ImmutableArray<int> MissingCodepoints);

/// <summary>
/// 글자 배치(A6). HarfBuzz로 문단 전체를 셰이핑(커닝 포함)한 뒤 상자 폭에 맞춰 줄을 나눈다.
/// 줄 끊기 우선순위: 마지막 공백, 한글·한자·가나 사이, 아무 글자 사이. 줄 끝 공백은 줄 폭에 넣지 않고 출력하지 않는다.
/// 글꼴에 없는 글자는 배치하지 않고 <see cref="LaidOutText.MissingCodepoints"/>에 모은다(.notdef를 그리지 않는다).
/// </summary>
public static class TextLayout
{
    private sealed record Unit(int First, int Count, float Width, int Cp, string Text)
    {
        public bool IsSpace => Cp == ' ';
        public bool IsCjk => IsCjkCodepoint(Cp);
    }

    private readonly record struct Glyph(int Gid, float Advance, float Nominal, int Font = 0);

    private sealed record ShapedRun(Glyph[] Glyphs, Unit[] Units);

    public static LaidOutText Layout(AppFont font, string text, float fontSizePt, float boxWidthPt, TextAlign align,
        PdfPointF topLeft, float lineHeightFactor = 1.2f)
        => Layout([font], text, fontSizePt, boxWidthPt, align, topLeft, lineHeightFactor);

    /// <summary>
    /// 글자별 글꼴 대체(0.13.0). fonts[0]이 고른 글꼴, 뒤는 대체 글꼴이다. 각 글자는 그것을 가진 첫 글꼴로 그린다.
    /// 공백·문장부호는 앞 글자의 글꼴을 따라 구간이 잘게 쪼개지지 않는다(문단 맨 앞이면 뒤 글자를 따른다).
    /// 줄 높이·첫 기준선은 fonts[0] 기준. 어느 글꼴에도 없는 글자만 MissingCodepoints에 모은다.
    /// 폭 재기·줄바꿈·그리기·저장이 모두 이 한 배치를 쓴다.
    /// </summary>
    public static LaidOutText Layout(IReadOnlyList<AppFont> fonts, string text, float fontSizePt, float boxWidthPt, TextAlign align,
        PdfPointF topLeft, float lineHeightFactor = 1.2f)
    {
        var font = fonts[0];
        float lineHeight = fontSizePt * lineHeightFactor;
        bool finite = float.IsFinite(boxWidthPt);
        float contentH = (font.Ascender1000 - font.Descender1000) * fontSizePt / 1000f;
        float firstBaseline = topLeft.Y - (lineHeight - contentH) / 2f - font.Ascender1000 * fontSizePt / 1000f;

        var missing = new SortedSet<int>();
        var spans = new List<(ShapedRun Run, int From, int To)>();
        foreach (var para in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var run = Shape(fonts, para, fontSizePt, missing);
            if (run.Units.Length == 0) { spans.Add((run, 0, 0)); continue; }
            foreach (var (from, to) in Break(run.Units, finite ? boxWidthPt : float.PositiveInfinity))
                spans.Add((run, from, to));
        }

        // 줄 폭(끝 공백 제외)
        var widths = new float[spans.Count];
        var trimmedTo = new int[spans.Count];
        for (int i = 0; i < spans.Count; i++)
        {
            var (run, from, to) = spans[i];
            int t = to;
            while (t > from && run.Units[t - 1].IsSpace) t--;
            trimmedTo[i] = t;
            float w = 0;
            for (int u = from; u < t; u++) w += run.Units[u].Width;
            widths[i] = w;
        }
        float avail = finite ? boxWidthPt : (widths.Length == 0 ? 0 : widths.Max());

        var lines = ImmutableArray.CreateBuilder<ImmutableArray<PositionedGlyph>>(spans.Count);
        float minX = float.MaxValue, maxX = float.MinValue;
        for (int i = 0; i < spans.Count; i++)
        {
            var (run, from, _) = spans[i];
            float offset = align switch
            {
                TextAlign.Center => (avail - widths[i]) / 2f,
                TextAlign.Right => avail - widths[i],
                _ => 0f,
            };
            float x = topLeft.X + offset;
            float y = firstBaseline - i * lineHeight;
            var gl = ImmutableArray.CreateBuilder<PositionedGlyph>();
            for (int u = from; u < trimmedTo[i]; u++)
            {
                var un = run.Units[u];
                for (int g = 0; g < un.Count; g++)
                {
                    var sg = run.Glyphs[un.First + g];
                    gl.Add(new PositionedGlyph(sg.Gid, fonts[sg.Font].GetCid(sg.Gid), new PdfPointF(x, y), sg.Nominal,
                        g == 0 ? un.Text : "") { FontIndex = sg.Font });
                    x += sg.Advance;
                }
            }
            if (gl.Count > 0)
            {
                minX = Math.Min(minX, topLeft.X + offset);
                maxX = Math.Max(maxX, x);
            }
            lines.Add(gl.ToImmutable());
        }
        if (minX > maxX) minX = maxX = topLeft.X;
        var bounds = new PdfRectF(minX, topLeft.Y - spans.Count * lineHeight, maxX, topLeft.Y);
        return new LaidOutText(lines.ToImmutable(), bounds, [.. missing]);
    }

    /// <summary>
    /// 한 줄 글이 글꼴 목록 가운데 어느 글꼴로 나뉘는지(글자 순서대로 글꼴 번호, 어느 글꼴에도 없으면 -1).
    /// 공백·문장부호는 앞 글자를 따른다. 배치(<see cref="Layout(IReadOnlyList{AppFont}, string, float, float, TextAlign, PdfPointF, float)"/>)와 같은 규칙이다.
    /// </summary>
    public static int[] PickFonts(IReadOnlyList<AppFont> fonts, IReadOnlyList<int> cps)
    {
        var pick = new int[cps.Count];
        for (int i = 0; i < cps.Count; i++)
            pick[i] = FirstCovering(fonts, cps[i]);
        if (fonts.Count > 1)
        {
            for (int i = 0; i < cps.Count; i++)
            {
                if (!IsNeutral(cps[i])) continue;
                int want = -1;
                for (int k = i - 1; k >= 0 && want < 0; k--) if (pick[k] >= 0) want = pick[k];
                if (want < 0) for (int k = i + 1; k < cps.Count && want < 0; k++) if (!IsNeutral(cps[k]) && pick[k] >= 0) want = pick[k];
                if (want >= 0 && fonts[want].GetGlyph(cps[i]) is not null) pick[i] = want;
            }
        }
        return pick;
    }

    /// <summary>문단을 글꼴 구간으로 나눠 구간마다 셰이핑하고 클러스터 단위로 묶는다. 어느 글꼴에도 없는 글자는 빼고 missing에 기록.</summary>
    private static ShapedRun Shape(IReadOnlyList<AppFont> fonts, string para, float size, SortedSet<int> missing)
    {
        var cps = new List<int>();
        foreach (var r in para.EnumerateRunes())
        {
            int cp = r.Value;
            if (cp == '\t') cp = ' ';
            if (IsIgnorable(cp)) continue;
            cps.Add(cp);
        }
        var pick = PickFonts(fonts, cps);
        var glyphs = new List<Glyph>();
        var units = new List<Unit>();
        int from = 0, segFont = -1;
        for (int i = 0; i < cps.Count; i++)
        {
            if (pick[i] < 0) { missing.Add(cps[i]); continue; }
            if (segFont >= 0 && pick[i] != segFont)
            {
                AppendSegment(fonts[segFont], segFont, cps, pick, from, i, size, glyphs, units);
                from = i;
            }
            segFont = pick[i];
        }
        if (segFont >= 0) AppendSegment(fonts[segFont], segFont, cps, pick, from, cps.Count, size, glyphs, units);
        return new ShapedRun([.. glyphs], [.. units]);
    }

    private static int FirstCovering(IReadOnlyList<AppFont> fonts, int cp)
    {
        for (int f = 0; f < fonts.Count; f++) if (fonts[f].GetGlyph(cp) is not null) return f;
        return -1;
    }

    internal static bool IsNeutral(int cp)
    {
        if (cp > 0xFFFF) return false;
        var cat = char.GetUnicodeCategory((char)cp);
        return char.IsWhiteSpace((char)cp) || cat is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    }

    /// <summary>[from, to) 글자(어느 글꼴에도 없는 글자는 뺀다)를 한 글꼴로 셰이핑해 glyphs·units에 이어 붙인다.</summary>
    private static void AppendSegment(AppFont font, int fi, List<int> cps, int[] pick, int from, int to, float size, List<Glyph> glyphs, List<Unit> units)
    {
        var sb = new StringBuilder();
        for (int k = from; k < to; k++) if (pick[k] >= 0) sb.Append(char.ConvertFromUtf32(cps[k]));
        string clean = sb.ToString();
        if (clean.Length == 0) return;
        var shaped = font.Shape(clean);
        int baseIdx = glyphs.Count;
        for (int i = 0; i < shaped.Length; i++)
            glyphs.Add(new Glyph(shaped[i].Gid, shaped[i].Advance1000 * size / 1000f, font.Advance1000(shaped[i].Gid) * size / 1000f, fi));

        var clusters = shaped.Select(g => g.Cluster).Distinct().OrderBy(c => c).ToList();
        var nextCluster = new Dictionary<int, int>(clusters.Count);
        for (int k = 0; k < clusters.Count; k++) nextCluster[clusters[k]] = k + 1 < clusters.Count ? clusters[k + 1] : clean.Length;
        int start = 0;
        while (start < shaped.Length)
        {
            int cl = shaped[start].Cluster, end = start + 1;
            float w = glyphs[baseIdx + start].Advance;
            while (end < shaped.Length && shaped[end].Cluster == cl) w += glyphs[baseIdx + end++].Advance;
            int next = nextCluster[cl];
            string t = clean.Substring(cl, Math.Max(0, next - cl));
            units.Add(new Unit(baseIdx + start, end - start, w, char.ConvertToUtf32(clean, cl), t));
            start = end;
        }
    }

    /// <summary>단위 구간 [from, to) 목록. 끝 공백은 구간에 남아 있다(줄 폭 계산에서 제외).</summary>
    private static List<(int From, int To)> Break(Unit[] units, float box)
    {
        var result = new List<(int, int)>();
        int lineStart = 0, lastSpace = -1, lastCjk = -1;
        float curW = 0;
        bool hasVisible = false;
        for (int u = 0; u < units.Length;)
        {
            var un = units[u];
            if (un.IsSpace)
            {
                curW += un.Width;
                u++;
                continue;
            }
            if (u > lineStart)
            {
                var prev = units[u - 1];
                if (prev.IsSpace) lastSpace = u;
                else if (prev.IsCjk || un.IsCjk) lastCjk = u;
            }
            if (hasVisible && curW + un.Width > box + 1e-3f)
            {
                int br = lastSpace > lineStart ? lastSpace : lastCjk > lineStart ? lastCjk : u;
                result.Add((lineStart, br));
                lineStart = u = br;
                lastSpace = lastCjk = -1;
                curW = 0;
                hasVisible = false;
                continue;
            }
            curW += un.Width;
            hasVisible = true;
            u++;
        }
        result.Add((lineStart, units.Length));
        return result;
    }

    private static bool IsIgnorable(int cp) =>
        cp < 0x20 || (cp >= 0x7F && cp < 0xA0)
        || (cp >= 0x200B && cp <= 0x200F) || cp == 0x2060 || cp == 0xFEFF
        || (cp >= 0xFE00 && cp <= 0xFE0F) || (cp >= 0xE0100 && cp <= 0xE01EF);

    private static bool IsCjkCodepoint(int cp) =>
        (cp >= 0x1100 && cp <= 0x11FF) || (cp >= 0x2E80 && cp <= 0x303F) || (cp >= 0x3040 && cp <= 0x30FF)
        || (cp >= 0x3130 && cp <= 0x318F) || (cp >= 0x3400 && cp <= 0x4DBF) || (cp >= 0x4E00 && cp <= 0x9FFF)
        || (cp >= 0xA960 && cp <= 0xA97F) || (cp >= 0xAC00 && cp <= 0xD7FF) || (cp >= 0xF900 && cp <= 0xFAFF)
        || (cp >= 0xFF00 && cp <= 0xFFEF) || (cp >= 0x20000 && cp <= 0x2FA1F);
}
