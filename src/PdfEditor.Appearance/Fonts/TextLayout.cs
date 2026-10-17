using System.Collections.Immutable;
using System.Text;
using Pdf.Native;

namespace PdfEditor.Appearance.Fonts;

public enum TextAlign { Left, Center, Right }

/// <summary>
/// 배치된 글리프 하나. <see cref="Origin"/>은 기준선 위의 왼쪽 점(PDF 사용자 좌표, 커닝 반영).
/// <see cref="Advance"/>는 글꼴이 정한 글리프 폭(pt, 커닝 제외)이라 PDF /W에 그대로 쓴다.
/// <see cref="Text"/>는 이 글리프가 처음 나타내는 원문(합자의 둘째 글리프 등은 빈 문자열).
/// </summary>
public sealed record PositionedGlyph(int Gid, int Cid, PdfPointF Origin, float Advance, string Text);

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

    private readonly record struct Glyph(int Gid, float Advance, float Nominal);

    private sealed record ShapedRun(Glyph[] Glyphs, Unit[] Units);

    public static LaidOutText Layout(AppFont font, string text, float fontSizePt, float boxWidthPt, TextAlign align,
        PdfPointF topLeft, float lineHeightFactor = 1.2f)
    {
        float lineHeight = fontSizePt * lineHeightFactor;
        bool finite = float.IsFinite(boxWidthPt);
        float contentH = (font.Ascender1000 - font.Descender1000) * fontSizePt / 1000f;
        float firstBaseline = topLeft.Y - (lineHeight - contentH) / 2f - font.Ascender1000 * fontSizePt / 1000f;

        var missing = new SortedSet<int>();
        var spans = new List<(ShapedRun Run, int From, int To)>();
        foreach (var para in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var run = Shape(font, para, fontSizePt, missing);
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
                    gl.Add(new PositionedGlyph(sg.Gid, font.GetCid(sg.Gid), new PdfPointF(x, y), sg.Nominal,
                        g == 0 ? un.Text : ""));
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

    /// <summary>문단을 셰이핑해 클러스터 단위로 묶는다. 글꼴에 없는 글자는 빼고 missing에 기록.</summary>
    private static ShapedRun Shape(AppFont font, string para, float size, SortedSet<int> missing)
    {
        var sb = new StringBuilder(para.Length);
        foreach (var r in para.EnumerateRunes())
        {
            int cp = r.Value;
            if (cp == '\t') cp = ' ';
            if (IsIgnorable(cp)) continue;
            if (font.GetGlyph(cp) is null) { missing.Add(cp); continue; }
            sb.Append(char.ConvertFromUtf32(cp));
        }
        string clean = sb.ToString();
        if (clean.Length == 0) return new ShapedRun([], []);

        var shaped = font.Shape(clean);
        var glyphs = new Glyph[shaped.Length];
        for (int i = 0; i < shaped.Length; i++)
            glyphs[i] = new Glyph(shaped[i].Gid, shaped[i].Advance1000 * size / 1000f,
                font.Advance1000(shaped[i].Gid) * size / 1000f);

        var clusters = shaped.Select(g => g.Cluster).Distinct().OrderBy(c => c).ToList();
        var units = new List<Unit>();
        int start = 0;
        while (start < shaped.Length)
        {
            int cl = shaped[start].Cluster, end = start + 1;
            float w = glyphs[start].Advance;
            while (end < shaped.Length && shaped[end].Cluster == cl) w += glyphs[end++].Advance;
            int idx = clusters.IndexOf(cl);
            int next = idx + 1 < clusters.Count ? clusters[idx + 1] : clean.Length;
            string t = clean.Substring(cl, Math.Max(0, next - cl));
            units.Add(new Unit(start, end - start, w, char.ConvertToUtf32(clean, cl), t));
            start = end;
        }
        return new ShapedRun(glyphs, [.. units]);
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
