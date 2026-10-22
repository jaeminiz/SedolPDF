using System.Collections.Immutable;
using System.Text;
using Pdf.Native;

namespace PdfEditor.TextEdit.Paragraphs;

public enum FontChoice { Original, App }

/// <summary>한글끼리 줄 끝-줄 시작을 이을 때·다시 끊을 때의 방식(V19: 문서마다 다르고 문서·열 단위 판정이 필요 — 판정은 부르는 쪽).</summary>
public enum KoreanLineBreak
{
    /// <summary>어절 단위 줄바꿈 문서: 줄 이음은 공백, 낱말 가운데서는 끊지 않는다(긴 낱말만 예외).</summary>
    Word,
    /// <summary>글자 단위 줄바꿈 문서: 줄 이음은 없음(붙임), 글자 사이 어디서나 끊는다.</summary>
    Character,
}

/// <summary>다시 맞추기 설정. 정렬은 원본 문단의 것을 쓴다(재정의 없음).</summary>
public sealed record LayoutOptions
{
    public KoreanLineBreak Korean { get; init; } = KoreanLineBreak.Word;

    /// <summary>쪽 경계(쪽 좌표). 있으면 이 아래로는 줄을 만들 수 없다(C6) — 넘으면 <see cref="ParagraphLayoutResult.Overflow"/>.</summary>
    public PdfRectF? PageBounds { get; init; }

    /// <summary>줄 폭 허용 여유(글자 크기 배수, 부동소수 오차 흡수용). 열 폭은 원래 줄을 같은 측정으로 잰 값을 기준으로 한다.</summary>
    public double WidthSlackEm { get; init; } = 0.02;

    /// <summary>줄 머리에 오면 안 되는 글자(약식 금칙).</summary>
    public string NoLineStart { get; init; } = ".,;:!?)]}」』”’%·、。，．";

    /// <summary>줄 끝에 오면 안 되는 글자.</summary>
    public string NoLineEnd { get; init; } = "([{「『“‘";

    /// <summary>쪽 아래 경계 계산에서 기준선 아래로 필요한 여백(글자 크기 배수).</summary>
    public double DescentEm { get; init; } = 0.25;
}

/// <summary>
/// 다시 맞춘 줄 하나. <see cref="BaselineOrigin"/>은 쪽 좌표의 기준선 시작점(글이 흐르는 쪽의 왼쪽 끝).
/// <see cref="JustifyExtra"/>는 양쪽 맞춤에서 공백마다 더 벌릴 이동량(글 공간 단위, Tz 적용 뒤).
/// </summary>
public sealed record LaidLine(string Text, PdfPointF BaselineOrigin, float Width, bool IsOriginal, int OriginalLineIndex, float JustifyExtra = 0);

/// <summary>
/// <see cref="Lines"/> = 문단의 전체 줄(위→아래). [<see cref="FirstRewritten"/>, <see cref="LastRewritten"/>] 줄만 새 줄이고 그 밖은 원본 줄 그대로다
/// (<c>FirstRewritten &gt; LastRewritten</c>이면 바뀐 줄 없음). 원래 줄 수가 <see cref="OriginalLineCount"/>이고 새 줄 수가 더 적으면 남는 원본 줄은 지워진다(아래가 빔).
/// </summary>
public sealed record ParagraphLayoutResult(ImmutableArray<LaidLine> Lines, int FirstRewritten, int LastRewritten, int MaxLines, bool Overflow,
    FontChoice Choice, ImmutableArray<int> MissingCodepoints, bool Overlap = false, bool DeleteAll = false, int OriginalLineCount = 0, int Angle = 0)
{
    public bool NoChange => !DeleteAll && FirstRewritten > LastRewritten && Lines.Length == OriginalLineCount;

    /// <summary>다시 쓰는 줄의 글(이어 붙인 것) — 글꼴이 그릴 수 있는지 확인하는 데 쓴다.</summary>
    public string RewrittenText => string.Concat(Lines.Skip(FirstRewritten).Take(Math.Max(0, LastRewritten - FirstRewritten + 1)).Select(l => l.Text));
}

/// <summary>
/// 문단 다시 맞추기(M5 C5·C6·C10·C15·C16): 편집 창 글 → 줄. 바뀐 줄부터 줄바꿈을 다시 하되 원래 줄 끝과 다시 맞는 곳부터는 원본 줄을 그대로 둔다.
/// 순수 계산(PDFium 없음). 폭은 <see cref="ITextMeasure"/>가 준다.
/// </summary>
public static class ParagraphLayout
{
    /// <summary>
    /// 줄을 합친 문단 글(편집 창에 처음 보여 줄 글). 줄 사이는 공백이고, 글자 단위 줄바꿈 문서에서 한글끼리 이어지는 곳만 붙인다.
    /// </summary>
    public static string OriginalText(ParagraphModel p, LayoutOptions? options = null)
    {
        var opt = options ?? new LayoutOptions();
        var sb = new StringBuilder();
        for (int i = 0; i < p.Lines.Length; i++)
        {
            string t = p.Lines[i].Text;
            if (i > 0 && !JoinsWithoutSpace(sb, t, opt)) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }

    private static bool JoinsWithoutSpace(StringBuilder prev, string next, LayoutOptions opt) =>
        opt.Korean == KoreanLineBreak.Character && prev.Length > 0 && next.Length > 0 && IsCjk(prev[^1]) && IsCjk(next[0]);

    public static ParagraphLayoutResult Layout(ParagraphModel p, string newText, ITextMeasure? original, ITextMeasure? app, LayoutOptions? options = null)
    {
        if (original is null && app is null) throw new ArgumentException("at least one text measure is required");
        var opt = options ?? new LayoutOptions();
        string text = Normalize(newText);
        string orig = OriginalText(p, opt);
        int n = p.Lines.Length;

        if (text == orig) return new ParagraphLayoutResult(OriginalLines(p, 0, n), 0, -1, MaxLinesOf(p, opt), false, FontChoice.Original, [], false, false, n, p.Lines[0].Angle);
        if (text.Trim().Length == 0)
            return new ParagraphLayoutResult([], 0, -1, MaxLinesOf(p, opt), false, FontChoice.Original, [], false, true, n, p.Lines[0].Angle);

        if (original is not null)
        {
            var r = Run(p, text, orig, original, FontChoice.Original, opt);
            if (original.CanRender(r.RewrittenText, out _)) return r;
            if (app is null) { original.CanRender(r.RewrittenText, out var miss); return r with { MissingCodepoints = miss }; }
        }
        var r2 = Run(p, text, orig, app!, FontChoice.App, opt);
        app!.CanRender(r2.RewrittenText, out var missing);
        return r2 with { MissingCodepoints = missing };
    }

    // ------------------------------------------------------------------ 본체
    private static ParagraphLayoutResult Run(ParagraphModel p, string text, string orig, ITextMeasure measure, FontChoice choice, LayoutOptions opt)
    {
        int n = p.Lines.Length, angle = p.Lines[0].Angle;
        float size = p.Style.SizePt;
        double tz = p.Style.Tz / 100.0, tc = p.Lines[0].Runs.IsDefaultOrEmpty ? 0 : p.Lines[0].Runs[0].Tc;
        double W(string s) => (measure.Advance(s, size) + tc * s.Length) * tz;

        var (uLeft, uRight, _, _) = Geo.Project(p.Box, angle);
        double indent = p.Align is Alignment.Left or Alignment.Justified ? p.FirstLineIndent : 0;
        // 열 폭: 상자(글자 잉크) 폭과, 같은 측정으로 잰 원래 줄 중 가장 긴 폭 중 큰 값 — 원래 줄이 모두 들어가야 하고, 잉크 폭은 글자 여백만큼 이동량보다 작다.
        double colW = uRight - uLeft;
        for (int i = 0; i < p.Lines.Length; i++) colW = Math.Max(colW, W(p.Lines[i].Text) + (i == 0 ? indent : 0));
        double Avail(int lineIndex) => colW - (lineIndex == 0 ? indent : 0) + opt.WidthSlackEm * size;

        // 원래 줄 시작 오프셋
        var starts = new int[n];
        {
            int off = 0;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) off += SepLength(p, i, opt);
                starts[i] = off;
                off += p.Lines[i].Text.Length;
            }
        }

        // 바뀐 구간: 공통 앞부분 pre, 공통 뒷부분 suf
        int min = Math.Min(text.Length, orig.Length), pre = 0;
        while (pre < min && text[pre] == orig[pre]) pre++;
        int suf = 0;
        while (suf < min - pre && text[text.Length - 1 - suf] == orig[orig.Length - 1 - suf]) suf++;
        int delta = text.Length - orig.Length, origChangedEnd = orig.Length - suf, newChangedEnd = text.Length - suf;

        // 처음 다시 쓸 줄: 바뀐 곳이 들어 있는 줄(줄 맨 앞에서 바뀌면 앞 줄이 받아 줄일 수 있으므로 앞 줄부터)
        int k = 0;
        for (int i = 0; i < n; i++) if (starts[i] <= pre) k = i;
        if (k > 0 && pre <= starts[k])
        {
            // 줄 맨 앞이 바뀌었을 때만: 앞 줄이 새로 시작하는 첫 낱말을 받을 수 있으면 앞 줄부터 다시 맞춘다
            int e = pre;
            while (e < text.Length && text[e] is not (' ' or '\n')) e++;
            if (W(p.Lines[k - 1].Text + " " + text[pre..e]) <= Avail(k - 1)) k--;
        }

        var lines = ImmutableArray.CreateBuilder<LaidLine>();
        for (int i = 0; i < k; i++) lines.Add(OriginalLine(p, i));

        double Baseline(int i) => i < n ? p.Lines[i].Baseline : p.Lines[n - 1].Baseline - (i - n + 1) * Leading(p);
        var (_, _, vx, vy) = Geo.Axes(angle);
        var (ux, uy, _, _) = Geo.Axes(angle);
        PdfPointF Page(double u, double v) => new((float)(u * ux + v * vx), (float)(u * uy + v * vy));

        int first = k, idx = k, st = starts[k];
        bool synced = false;
        while (true)
        {
            var br = NextBreak(text, st, Avail(idx), W, opt);
            string lineText = text[st..br.End].TrimEnd(' ');
            double width = W(lineText);
            bool lastOfPara = br.Next >= text.Length;
            float extra = 0;
            double u = p.Align switch
            {
                Alignment.Center => (uLeft + uRight) / 2 - width / 2,
                Alignment.Right => uRight - width,
                _ => uLeft + (idx == 0 ? indent : 0),
            };
            if (p.Align == Alignment.Justified && !br.Forced && !lastOfPara)
            {
                int spaces = lineText.Count(c => c == ' ');
                double room = Avail(idx) - opt.WidthSlackEm * size - width;
                if (spaces > 0 && room > 0) { extra = (float)(room / spaces); }
            }
            lines.Add(new LaidLine(lineText, Page(u, Baseline(idx)), (float)width, false, -1, extra));

            if (lastOfPara) break;
            // 원래 줄 끝과 다시 맞는가: 줄 번호가 같고, 바뀐 구간 뒤이며, 다음 줄 시작이 원래 줄 시작(바뀐 글자 수만큼 이동)과 같다
            int j = idx + 1;
            if (j < n && br.Next >= newChangedEnd && br.Next - delta == starts[j] && br.Next - delta >= origChangedEnd)
            {
                synced = true;
                break;
            }
            idx++;
            st = br.Next;
        }

        int last = lines.Count - 1;
        if (synced) for (int i = last + 1; i < n; i++) lines.Add(OriginalLine(p, i));

        // 쪽 아래·겹침
        int maxLines = MaxLinesOf(p, opt);
        bool overflow = lines.Count > maxLines;
        bool overlap = false;
        if (!float.IsNegativeInfinity(p.MaxBottom))
        {
            double bottom = Baseline(lines.Count - 1) - opt.DescentEm * size;
            overlap = bottom < p.MaxBottom;
        }
        return new ParagraphLayoutResult(lines.ToImmutable(), first, last, maxLines, overflow, choice, [], overlap, false, n, angle);
    }

    private static int SepLength(ParagraphModel p, int i, LayoutOptions opt)
    {
        string a = p.Lines[i - 1].Text, b = p.Lines[i].Text;
        return opt.Korean == KoreanLineBreak.Character && a.Length > 0 && b.Length > 0 && IsCjk(a[^1]) && IsCjk(b[0]) ? 0 : 1;
    }

    private static float Leading(ParagraphModel p) => p.Leading > 0 ? p.Leading : p.Style.SizePt * 1.2f;

    private static int MaxLinesOf(ParagraphModel p, LayoutOptions opt)
    {
        if (opt.PageBounds is not { } page) return int.MaxValue;
        int angle = p.Lines[0].Angle, n = p.Lines.Length;
        var (_, _, v0, _) = Geo.Project(page, angle);
        float size = p.Style.SizePt;
        double lead = Leading(p);
        int count = 0;
        for (int i = 0; i < 100000; i++)
        {
            double b = i < n ? p.Lines[i].Baseline : p.Lines[n - 1].Baseline - (i - n + 1) * lead;
            if (b - opt.DescentEm * size < v0) break;
            count++;
        }
        return count;
    }

    // ------------------------------------------------------------------ 원본 줄
    private static ImmutableArray<LaidLine> OriginalLines(ParagraphModel p, int from, int to)
    {
        var b = ImmutableArray.CreateBuilder<LaidLine>();
        for (int i = from; i < to; i++) b.Add(OriginalLine(p, i));
        return b.ToImmutable();
    }

    private static LaidLine OriginalLine(ParagraphModel p, int i)
    {
        var l = p.Lines[i];
        var (ux, uy, vx, vy) = Geo.Axes(l.Angle);
        var origin = new PdfPointF((float)(l.U0 * ux + l.Baseline * vx), (float)(l.U0 * uy + l.Baseline * vy));
        return new LaidLine(l.Text, origin, (float)l.Width, true, i);
    }

    // ------------------------------------------------------------------ 줄바꿈 (C16)
    private readonly record struct Break(int End, int Next, bool Forced);

    private static Break NextBreak(string text, int st, double avail, Func<string, double> W, LayoutOptions opt)
    {
        int len = text.Length;
        int hard = text.IndexOf('\n', st);
        int limitEnd = hard < 0 ? len : hard;
        bool Fits(int end) => W(text[st..end].TrimEnd(' ')) <= avail;
        if (Fits(limitEnd)) return new Break(limitEnd, hard < 0 ? len : hard + 1, hard >= 0);

        // 들어가는 가장 큰 끝(글자 단위). 폭은 길어질수록 커진다고 보고 이분 탐색.
        int lo = st + 1, hi = limitEnd - 1;           // limitEnd는 안 들어감
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Fits(mid)) lo = mid; else hi = mid - 1;
        }
        int f = Math.Max(lo, st + 1);
        if (f >= limitEnd) return new Break(limitEnd, hard < 0 ? len : hard + 1, hard >= 0);

        int end;
        if (text[f] == ' ') end = f;
        else if (opt.Korean == KoreanLineBreak.Character && IsCjk(text[f - 1]) && IsCjk(text[f])) end = Kinsoku(text, st, f, opt);
        else
        {
            int q = f - 1 > st ? text.LastIndexOf(' ', f - 1, f - st) : -1;
            end = q > st ? q : Kinsoku(text, st, f, opt);
        }
        int next = end;
        while (next < len && text[next] == ' ') next++;
        return new Break(end, next, false);
    }

    private static int Kinsoku(string text, int st, int end, LayoutOptions opt)
    {
        while (end > st + 1 && (opt.NoLineStart.Contains(text[end]) || opt.NoLineEnd.Contains(text[end - 1]))) end--;
        return end;
    }

    internal static bool IsCjk(char c) =>
        c is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏'
          or >= '一' and <= '鿿' or >= '぀' and <= 'ヿ' or >= '㐀' and <= '䶿';

    private static string Normalize(string s)
    {
        string t = s.Replace("\r\n", "\n").Replace('\r', '\n');
        return t.TrimEnd('\n', ' ');
    }
}
