using System.Collections.Immutable;
using PdfEditor.Document;

namespace PdfEditor.TextEdit.FindReplace;

/// <summary>찾기 옵션(D10). 정규식은 범위 밖(후보).</summary>
public sealed record FindOptions
{
    public string Query { get; init; } = "";
    /// <summary>대소문자 구분(기본 끔).</summary>
    public bool MatchCase { get; init; }
    /// <summary>완전일치: 앞뒤가 낱말 글자가 아닐 때만 찾는다.</summary>
    public bool WholeWord { get; init; }
    /// <summary>
    /// 완전일치에서 `-`·`_`와 앞뒤가 글자·숫자인 `.`을 낱말 글자로 본다(U3, 기본 켜짐) — `H-2501`·`A.12`가 통째로 한 낱말이 된다. 끄면 글자·숫자만 낱말 글자.
    /// </summary>
    public bool DrawingIdWordChars { get; init; } = true;
    /// <summary>가로(0°) 말고 회전된 글(표제란 90° 등)도 찾는다(기본 켬).</summary>
    public bool IncludeRotated { get; init; } = true;
    /// <summary>쪽 공간 사각형 안만 찾는다(현재 화면 영역). null이면 쪽 전체.</summary>
    public (double L, double B, double R, double T)? Region { get; init; }
}

/// <summary>글 연산 안의 연속한 글 코드 범위(Str번째 문자열의 FromCode부터 CodeCount개). StreamId 0 = 쪽, 그 밖 = 폼.</summary>
public readonly record struct GlyphSpanRef(int StreamId, int Op, int Str, int FromCode, int CodeCount);

/// <summary>찾은 곳 하나. Glyphs는 실제 글자(가상 공백 제외)만, 읽기 순서.</summary>
public sealed record TextMatch(PageId Page, int PageIndex, ImmutableArray<GlyphSpanRef> Spans, IReadOnlyList<GlyphInfo> Glyphs,
    string Text, string BeforeContext, string AfterContext, (double L, double B, double R, double T) Bounds, int RotationDeg, int LineIndex)
{
    /// <summary>폼 XObject 안의 글자가 있다 — 바꿀 수 없다(V15).</summary>
    public bool InForm => Glyphs.Any(g => g.InForm);

    /// <summary>`'`·`"` 연산자로 보인 글자가 있다 — 바꾸기 미지원.</summary>
    public bool HasQuoteOp => Glyphs.Any(g => g.QuoteOp);
}

/// <summary>논리 글줄 위의 문자열 찾기(0.4.0 D9·D10). PDFium을 부르지 않는 일반 스레드 코드.</summary>
public static class TextMatcher
{
    private const int ContextChars = 12;

    public static IReadOnlyList<TextMatch> Find(IReadOnlyList<LogicalLine> lines, FindOptions opts, PageId page = default, int pageIndex = -1)
    {
        var res = new List<TextMatch>();
        if (string.IsNullOrEmpty(opts.Query)) return res;
        var cmp = opts.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int li = 0; li < lines.Count; li++)
        {
            var ln = lines[li];
            if (!opts.IncludeRotated && ln.Angle != 0) continue;
            for (int at = ln.Text.IndexOf(opts.Query, cmp); at >= 0; at = ln.Text.IndexOf(opts.Query, at + opts.Query.Length, cmp))
            {
                if (opts.WholeWord && !WordBoundary(ln.Text, at, opts.Query.Length, opts.DrawingIdWordChars)) continue;
                // 여러 글자짜리 글자(합자)의 중간에서 시작하거나 끝나면 건너뛴다.
                if (SplitsGlyph(ln, at) || SplitsGlyph(ln, at + opts.Query.Length)) continue;
                var gs = new List<GlyphInfo>();
                for (int k = at; k < at + opts.Query.Length; k++)
                {
                    var g = ln.Glyphs[ln.Map[k]];
                    if (!g.Virtual && (gs.Count == 0 || !ReferenceEquals(gs[^1], g))) gs.Add(g);
                }
                if (gs.Count == 0) continue;
                var bounds = BoundsOf(gs);
                if (opts.Region is { } rg && !(Center(bounds) is var (cx, cy) && cx >= rg.L && cx <= rg.R && cy >= rg.B && cy <= rg.T)) continue;
                int from = Math.Max(0, at - ContextChars), to = Math.Min(ln.Text.Length, at + opts.Query.Length + ContextChars);
                res.Add(new TextMatch(page, pageIndex, SpansOf(gs), gs, ln.Text.Substring(at, opts.Query.Length), ln.Text[from..at],
                    ln.Text[(at + opts.Query.Length)..to], bounds, gs[0].Angle, li));
            }
        }
        // 읽는 순서: 위에서 아래, 왼쪽에서 오른쪽
        return res.OrderByDescending(m => Math.Round(m.Bounds.T / 2)).ThenBy(m => m.Bounds.L).ToList();
    }

    private static (double, double) Center((double L, double B, double R, double T) b) => ((b.L + b.R) / 2, (b.B + b.T) / 2);

    private static bool SplitsGlyph(LogicalLine ln, int charIndex)
    {
        if (charIndex <= 0 || charIndex >= ln.Map.Count) return false;
        return ln.Map[charIndex - 1] == ln.Map[charIndex] && !ln.Glyphs[ln.Map[charIndex]].Virtual;
    }

    internal static ImmutableArray<GlyphSpanRef> SpansOf(IReadOnlyList<GlyphInfo> gs)
    {
        var spans = ImmutableArray.CreateBuilder<GlyphSpanRef>();
        foreach (var g in gs)
        {
            if (spans.Count > 0 && spans[^1] is var last && last.StreamId == g.StreamId && last.Op == g.Op && last.Str == g.Str && last.FromCode + last.CodeCount == g.Code)
                spans[^1] = last with { CodeCount = last.CodeCount + 1 };
            else spans.Add(new GlyphSpanRef(g.StreamId, g.Op, g.Str, g.Code, 1));
        }
        return spans.ToImmutable();
    }

    internal static (double L, double B, double R, double T) BoundsOf(IEnumerable<GlyphInfo> gs)
    {
        double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
        foreach (var g in gs)
        {
            double th = g.Angle * Math.PI / 180, nx = -Math.Sin(th) * g.Size, ny = Math.Cos(th) * g.Size;
            // 글자 상자: 기준선에서 아래로 0.22 em, 위로 0.85 em
            foreach (var (x, y) in new[] { (g.X0 - 0.22 * nx, g.Y0 - 0.22 * ny), (g.X0 + 0.85 * nx, g.Y0 + 0.85 * ny), (g.X1 - 0.22 * nx, g.Y1 - 0.22 * ny), (g.X1 + 0.85 * nx, g.Y1 + 0.85 * ny) })
            {
                l = Math.Min(l, x); r = Math.Max(r, x); b = Math.Min(b, y); t = Math.Max(t, y);
            }
        }
        return (l, b, r, t);
    }

    private static bool WordBoundary(string t, int at, int len, bool idChars)
    {
        bool Word(int idx, int outward)
        {
            if (idx < 0 || idx >= t.Length) return false;
            char c = t[idx];
            if (char.IsLetterOrDigit(c)) return true;
            if (!idChars) return false;
            if (c is '-' or '_') return true;
            if (c == '.') { int nb = idx + outward; return nb >= 0 && nb < t.Length && char.IsLetterOrDigit(t[nb]); }   // '.'은 앞뒤가 글자·숫자일 때만 낱말 글자(A.12)
            return false;
        }
        return !Word(at - 1, -1) && !Word(at + len, +1);
    }
}
