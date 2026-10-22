using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.TextEdit.Paragraphs;

/// <summary>
/// 줄 → 문단 후보·정렬·들여쓰기·간격·신뢰도(M5 계획 C3 ②·C4). PDFium 없음, 일반 스레드. 같은 입력은 항상 같은 출력.
/// "틀리면 묶지 않는" 쪽으로 기울어 있다: 확신이 문턱 미만이면 한 줄 모델과 이유를 돌려준다(M4 한 줄 편집으로 되돌림).
/// </summary>
public static class ParagraphDetector
{
    /// <summary>
    /// <paramref name="seedLine"/> 줄이 속한 문단을 추정한다. 확신이 문턱 미만·표 행·섞인 줄이면 그 줄 하나짜리 모델(+이유)을 돌려준다.
    /// 인덱스가 범위 밖이면 null.
    /// </summary>
    public static ParagraphModel? Detect(IReadOnlyList<TextLine> lines, int seedLine, IReadOnlyList<Obstacle> obstacles, ParagraphOptions? options = null)
    {
        if (seedLine < 0 || seedLine >= lines.Count) return null;
        var opt = options ?? new ParagraphOptions();
        var obs = Filter(obstacles);
        var seed = lines[seedLine];
        if (seed.Mixed) return Build([seed], lines, obs, opt, ParagraphReasons.MixedStyle, 0);
        if (seed.TableRow) return Build([seed], lines, obs, opt, ParagraphReasons.TableRow, 0);

        var g = new List<TextLine> { seed };
        while (true)
        {
            var t = Neighbor(lines, g[^1], down: true, opt);
            if (t is null || WhyNot(lines, obs, g, t, down: true, opt) is not null) break;
            g.Add(t);
        }
        while (true)
        {
            var u = Neighbor(lines, g[0], down: false, opt);
            if (u is null || WhyNot(lines, obs, g, u, down: false, opt) is not null) break;
            g.Insert(0, u);
        }
        if (g.Count == 1) return Build(g, lines, obs, opt, null, 0);
        var (conf, _) = Score(lines, g, opt);
        return conf >= opt.Threshold
            ? Build(g, lines, obs, opt, null, conf)
            : Build([seed], lines, obs, opt, ParagraphReasons.LowConfidence, conf);
    }

    /// <summary>
    /// 사용자가 문단 상자 위·아래 단추로 연속한 줄을 더하거나(양수) 뺀다(음수). 위 끝은 <paramref name="topDelta"/>, 아래 끝은 <paramref name="bottomDelta"/>.
    /// 더할 때는 이웃 줄이 같은 각도로 바로 위/아래에 있기만 하면 되고(규칙 검사 없음 — 사용자가 직접 고른 것), 이웃이 없으면 거기서 멈춘다.
    /// 줄은 항상 1개 이상 남는다. 결과의 신뢰도는 다시 계산하며 이유는 없다(사용자가 만든 문단).
    /// </summary>
    public static ParagraphModel Extend(ParagraphModel p, int topDelta, int bottomDelta, IReadOnlyList<TextLine> lines, IReadOnlyList<Obstacle> obstacles, ParagraphOptions? options = null)
    {
        var opt = options ?? new ParagraphOptions();
        var obs = Filter(obstacles);
        var g = p.Lines.ToList();
        for (int i = 0; i < topDelta; i++)
        {
            var t = Neighbor(lines, g[0], down: false, opt);
            if (t is null || g.Contains(t)) break;
            g.Insert(0, t);
        }
        for (int i = 0; i < bottomDelta; i++)
        {
            var t = Neighbor(lines, g[^1], down: true, opt);
            if (t is null || g.Contains(t)) break;
            g.Add(t);
        }
        for (int i = 0; i < -topDelta && g.Count > 1; i++) g.RemoveAt(0);
        for (int i = 0; i < -bottomDelta && g.Count > 1; i++) g.RemoveAt(g.Count - 1);
        double conf = g.Count == 1 ? 0 : Score(lines, g, opt).Conf;
        return Build(g, lines, obs, opt, null, conf);
    }

    /// <summary>읽는 순서로 줄 전체를 문단으로 나눈다(시험·통계용). 확신이 문턱 미만인 줄은 혼자 남는다.</summary>
    public static IReadOnlyList<ParagraphModel> Partition(IReadOnlyList<TextLine> lines, IReadOnlyList<Obstacle> obstacles, ParagraphOptions? options = null)
    {
        var opt = options ?? new ParagraphOptions();
        var claimed = new HashSet<TextLine>();
        var result = new List<ParagraphModel>();
        var order = Enumerable.Range(0, lines.Count).OrderBy(i => lines[i].Angle).ThenByDescending(i => lines[i].Baseline).ThenBy(i => lines[i].U0);
        foreach (int i in order)
        {
            if (claimed.Contains(lines[i])) continue;
            var m = Detect(lines, i, obstacles, opt)!;
            if (m.IsParagraph && m.Lines.All(l => !claimed.Contains(l))) { foreach (var l in m.Lines) claimed.Add(l); result.Add(m); }
            else { claimed.Add(lines[i]); result.Add(m.Lines.Length == 1 ? m : Build([lines[i]], lines, Filter(obstacles), opt, m.LowConfidenceReason, m.Confidence)); }
        }
        return result;
    }

    private static IReadOnlyList<Obstacle> Filter(IReadOnlyList<Obstacle> obstacles) =>
        obstacles.Where(o => o.Kind != ObstacleKind.Frame).ToList();

    // ----------------------------------------------------------------- 이웃·묶기 규칙
    /// <summary>s의 바로 아래(위) 줄: 같은 각도, 가로로 겹치고, 가장 가까운 것. 비슷한 거리에 둘 이상이면(다단 등) null.</summary>
    internal static TextLine? Neighbor(IReadOnlyList<TextLine> lines, TextLine s, bool down, ParagraphOptions opt)
    {
        TextLine? best = null;
        double bestD = double.MaxValue;
        int tie = 0;
        foreach (var t in lines)
        {
            if (ReferenceEquals(t, s) || t.Angle != s.Angle) continue;
            double dv = down ? s.Baseline - t.Baseline : t.Baseline - s.Baseline;
            if (dv < opt.NeighborMinPitchEm * s.Size) continue;
            double ov = Math.Min(s.U1, t.U1) - Math.Max(s.U0, t.U0);
            if (ov < opt.NeighborOverlapFrac * Math.Min(s.Width, t.Width) || ov <= 0) continue;
            if (dv < bestD - opt.NeighborMinPitchEm * s.Size) { best = t; bestD = dv; tie = 0; }
            else if (Math.Abs(dv - bestD) <= opt.NeighborMinPitchEm * s.Size) tie++;
        }
        return tie > 0 ? null : best;
    }

    private static bool SameStyle(TextLine a, TextLine b, ParagraphOptions opt) =>
        !a.Mixed && !b.Mixed && a.FontKey == b.FontKey && a.Color == b.Color
        && Math.Abs(a.Size / b.Size - 1) <= opt.SizeTol && Math.Abs(a.Tz - b.Tz) <= opt.TzTol;

    /// <summary>t를 그룹에 더하지 못하는 이유(더할 수 있으면 null).</summary>
    internal static string? WhyNot(IReadOnlyList<TextLine> all, IReadOnlyList<Obstacle> obs, List<TextLine> g, TextLine t, bool down, ParagraphOptions opt)
    {
        var s = down ? g[^1] : g[0];
        if (!SameStyle(s, t, opt)) return "style";
        if (t.TableRow || s.TableRow) return "tablerow";
        double pitch = Math.Abs(s.Baseline - t.Baseline), em = s.Size;
        if (pitch < opt.MinPitchEm * em || pitch > opt.MaxPitchEm * em) return $"pitch {pitch / em:0.00}em";
        if (g.Count >= 2)
        {
            double p0 = Geo.Median(Pitches(g));
            if (Math.Abs(pitch - p0) > opt.PitchTol * p0) return "pitch inconsistent";
        }
        if (ObstacleBetween(obs, s, t)) return "obstacle";
        if (down && IsBulletStart(t)) return "bullet starts";
        bool tBullet = !down && IsBulletStart(t);
        if (!down && IsBulletStart(s) && g.Count >= 1) return "bullet is first";
        var cand = down ? g.Append(t).ToList() : g.Prepend(t).ToList();
        var cls = Classify(cand, opt);
        if (cls is null) return "alignment";
        if (cls is Alignment.Left or Alignment.Justified)
        {
            if (down)
            {
                double lmain = g.Count >= 2 ? Geo.Median(g.Skip(1).Select(x => x.U0)) : s.U0;
                if (!IsBulletStart(g[0]) && t.U0 - lmain > opt.IndentStartsParagraphEm * em) return "indented line starts paragraph";
            }
            else if (!tBullet)
            {
                double lmain = g.Count >= 2 ? Geo.Median(g.Skip(1).Select(x => x.U0)) : t.U0;
                if (g.Count >= 2 && g[0].U0 - lmain > opt.IndentStartsParagraphEm * em) return "first line is indented";
                if (g.Count == 1 && s.U0 - t.U0 > opt.IndentStartsParagraphEm * em) return "first line is indented";
            }
        }
        // 줄 폭이 열 폭의 절반 이하인 짧은 줄은 문단의 끝이다(이어 쓰는 줄바꿈은 다음 낱말이 안 들어갈 때만 생기므로 열 폭 근처까지 찬다)
        if (cls is Alignment.Left or Alignment.Justified)
        {
            double colR = all.Where(x => x.Angle == s.Angle && !x.Mixed && SameStyle(x, s, opt) && Math.Abs(x.U0 - s.U0) <= opt.ColumnNearEm * em).Max(x => x.U1);
            double colW = colR - Math.Min(s.U0, t.U0);
            bool Short(TextLine x) => colW > opt.ShortLineMinColumnEm * em && x.Width <= opt.ShortLineFrac * colW;
            if (down && Short(s)) return "short line ends the paragraph";
            if (!down && Short(t)) return "upper line is short (ends previous paragraph)";
        }
        double tol = opt.AlignTolEm * em;
        if (down && g.Count >= 3)
        {
            var body = g.Take(g.Count - 1).Select(x => x.U1).ToList();
            if (body.Max() - body.Min() <= tol && g[^1].U1 < Geo.Median(body) - opt.ShortLastLineEm * em) return "short last line";
        }
        if (!down && g.Count >= 2)
        {
            var body = g.Take(g.Count - 1).Select(x => x.U1).ToList();
            double rfull = g.Count == 2 ? g[0].U1 : Geo.Median(body);
            if ((g.Count == 2 || body.Max() - body.Min() <= tol) && t.U1 < rfull - opt.ShortLastLineEm * em) return "upper line short";
        }
        return null;
    }

    private static bool IsBulletStart(TextLine l) => LineAssembler.IsBulletStart(l.Text);

    /// <summary>줄 묶음의 정렬. 어느 것도 일관되지 않으면 null.</summary>
    internal static Alignment? Classify(List<TextLine> g, ParagraphOptions opt)
    {
        int n = g.Count;
        if (n == 1) return Alignment.Left;
        double em = g.Average(x => x.Size), tol = opt.AlignTolEm * em;
        // 오른쪽 끝이 모두 같고(마지막 줄 포함) 왼쪽 끝이 제각각이면 오른쪽 맞춤 — 왼쪽 맞춤 판정(앞줄 들여쓰기 허용)보다 먼저 본다
        double r0 = Geo.Median(g.Select(x => x.U1));
        // (앞줄만 다르고 나머지 왼쪽 끝이 같은 3줄 이상, 글머리표 줄은 오른쪽 맞춤이 아니라 들여쓰기 문단)
        if (g.All(x => Math.Abs(x.U1 - r0) <= tol) && g.Max(x => x.U0) - g.Min(x => x.U0) > tol
            && !LineAssembler.IsBulletStart(g[0].Text)
            && !(n >= 3 && g.Skip(1).Max(x => x.U0) - g.Skip(1).Min(x => x.U0) <= tol))
            return Alignment.Right;
        double lmain = Geo.Median(g.Skip(1).Select(x => x.U0));
        bool leftOk = g.Skip(1).All(x => Math.Abs(x.U0 - lmain) <= tol) && Math.Abs(g[0].U0 - lmain) <= opt.FirstLineIndentMaxEm * em;
        if (leftOk)
        {
            var body = g.Take(n - 1).Select(x => x.U1).ToList();
            return n >= 3 && body.Max() - body.Min() <= tol ? Alignment.Justified : Alignment.Left;
        }
        double c0 = Geo.Median(g.Select(x => (x.U0 + x.U1) / 2));
        if (g.All(x => Math.Abs((x.U0 + x.U1) / 2 - c0) <= tol * 1.2)) return Alignment.Center;
        return null;
    }

    private static bool ObstacleBetween(IReadOnlyList<Obstacle> obs, TextLine a, TextLine b)
    {
        var upper = a.Baseline > b.Baseline ? a : b;
        var lower = ReferenceEquals(upper, a) ? b : a;
        double vHi = upper.Baseline - 0.25 * upper.Size, vLo = lower.Baseline + 0.8 * lower.Size;
        if (vHi <= vLo) return false;
        double lo = Math.Min(a.U0, b.U0), hi = Math.Max(a.U1, b.U1);
        foreach (var o in obs)
        {
            if (o.Kind == ObstacleKind.Text) continue;                // 다른 글은 줄 조립이 이미 다룸
            var (ou0, ou1, ov0, ov1) = Geo.Project(o.Bounds, a.Angle);
            if (ov1 < vLo || ov0 > vHi) continue;                     // 줄 사이 띠와 세로로 안 겹침
            if (ou1 < lo || ou0 > hi) continue;                       // 가로로 안 겹침
            // 두 줄을 다 덮은 배경 그림은 장애물이 아니다
            if (o.Kind == ObstacleKind.Image && ou0 <= lo && ou1 >= hi && ov0 <= lower.Baseline - 0.3 * lower.Size && ov1 >= upper.Baseline + 0.8 * upper.Size) continue;
            return true;
        }
        return false;
    }

    // ----------------------------------------------------------------- 신뢰도 (C4)
    internal static (double Conf, Alignment Align) Score(IReadOnlyList<TextLine> all, List<TextLine> g, ParagraphOptions opt)
    {
        int n = g.Count;
        var align = Classify(g, opt) ?? Alignment.Left;
        if (n == 1) return (0, Alignment.Left);
        double c = n switch { 2 => opt.ScoreLines2, 3 => opt.ScoreLines3, 4 => opt.ScoreLines4, _ => opt.ScoreLines5Plus };
        var pit = Pitches(g);
        double mean = pit.Average();
        double cv = n >= 3 ? Math.Sqrt(pit.Average(p => (p - mean) * (p - mean))) / mean : 0;
        if (n >= 3) c += cv < opt.PitchCvTight ? opt.BonusPitchTight : cv < opt.PitchCvLoose ? opt.BonusPitchLoose : -opt.PenaltyPitchUneven;
        double em = g[0].Size, ratio = mean / em;
        if (ratio < opt.PitchOkMinEm || ratio > opt.PitchOkMaxEm) c -= opt.PenaltyPitchOutOfRange;
        if (n == 2 && ratio > opt.TwoLinePitchMaxEm) c -= opt.PenaltyTwoLineWidePitch;
        if (align == Alignment.Justified) c += opt.BonusJustified;
        if (align is Alignment.Left or Alignment.Justified)
        {
            double lmain = Geo.Median(g.Skip(1).Select(x => x.U0));
            double rstyle = all.Where(x => x.Angle == g[0].Angle && !x.Mixed && SameStyle(x, g[0], opt) && Math.Abs(x.U0 - lmain) <= opt.ColumnNearEm * em)
                .Select(x => x.U1).Append(g.Max(x => x.U1)).Max();
            double fullFrac = g.Take(n - 1).Count(x => x.U1 >= rstyle - opt.FullLineSlackEm * em) / (double)(n - 1);
            c += opt.BonusFullLines * fullFrac;
        }
        double avgEm = g.Average(x => x.Width / x.Size);
        if (avgEm < opt.ShortAvgEm) c -= opt.PenaltyShortAvg; else if (avgEm < opt.NarrowAvgEm) c -= opt.PenaltyNarrowAvg;   // 아주 짧은 줄(범례·라벨 목록)은 문단일 가능성이 낮다
        if (g.All(x => x.Text.Split(' ').Length <= opt.FewWordsMax)) c -= opt.PenaltyFewWords;
        // 목록 성격: 이음 줄이 대문자로 시작하는 라틴 글이 대부분이면 항목 나열일 가능성이 높다(전부 대문자 글은 판단 불가). 가운데 맞춤 4줄 이상도 문단보다 목록.
        var cont = g.Skip(1).Select(x => x.Text.TrimStart()).Where(t => t.Length > 0 && char.IsLetter(t[0]) && t[0] < 0x250).ToList();
        var letters = g.SelectMany(x => x.Text).Where(ch => ch < 0x250 && char.IsLetter(ch)).ToList();
        bool allCaps = letters.Count > 0 && letters.Count(char.IsUpper) >= opt.AllCapsFrac * letters.Count;
        if (cont.Count >= 2 && !allCaps && cont.Count(t => char.IsUpper(t[0])) >= opt.ListCapitalFrac * cont.Count) c -= opt.PenaltyListLike;
        if (align == Alignment.Center && n >= opt.CenterListMinLines) c -= opt.PenaltyCenterList;
        if (RepeatedPrefix(g, opt)) c -= opt.PenaltyRepeatedPrefix;
        if (g.Count(x => IsLabelLine(x.Text, opt)) >= opt.LabelLineFrac * n) c -= opt.PenaltyLabelLines;
        if (avgEm < opt.TinyAvgEm) c = Math.Min(c, opt.TinyAvgCap);
        return (Math.Clamp(c, 0, 1), align);
    }

    /// <summary>
    /// 줄 첫 낱말(숫자는 #로 바꾼 모양)이 같은 줄이 대부분 — "2 sets …", "1 set - …", "ANNEX …" 같은 항목 나열.
    /// 숫자나 대문자가 든 낱말만 센다(소문자 낱말 the·and 가 줄머리에 우연히 겹치는 보통 문단은 건드리지 않는다).
    /// </summary>
    internal static bool RepeatedPrefix(List<TextLine> g, ParagraphOptions opt)
    {
        if (g.Count < opt.RepeatedPrefixMinLines) return false;
        int most = g.Select(x =>
        {
            string t = x.Text.TrimStart();
            int sp = t.IndexOf(' ');
            string w = sp < 0 ? t : t[..sp];
            if (w.Length == 0 || w.Length > opt.PrefixTokenMaxChars || !w.Any(ch => char.IsDigit(ch) || char.IsUpper(ch))) return null;
            return new string(w.Select(ch => char.IsDigit(ch) ? '#' : char.ToLowerInvariant(ch)).ToArray());
        }).Where(s => s is not null).GroupBy(s => s).Select(k => k.Count()).DefaultIfEmpty(0).Max();
        return most >= Math.Max(opt.RepeatedPrefixMinCount, (int)Math.Ceiling(opt.RepeatedPrefixFrac * g.Count));
    }
    /// <summary>"Tel : 02-…", "the design pressure : 15 MPa" 처럼 앞쪽에 콜론이 있는 줄(라벨 : 값).</summary>
    internal static bool IsLabelLine(string text, ParagraphOptions opt)
    {
        int i = text.IndexOfAny([':', '：']);
        return i is > 0 && i <= opt.LabelMaxChars && i + 1 < text.Length && text[i + 1] == ' ';
    }

    internal static List<double> Pitches(List<TextLine> g)
    {
        var l = new List<double>();
        for (int i = 0; i + 1 < g.Count; i++) l.Add(Math.Abs(g[i].Baseline - g[i + 1].Baseline));
        return l;
    }

    // ----------------------------------------------------------------- 모델 만들기
    private static ParagraphModel Build(List<TextLine> g, IReadOnlyList<TextLine> all, IReadOnlyList<Obstacle> obs, ParagraphOptions opt, string? reason, double conf)
    {
        var first = g[0].Runs[0];
        var style = new ParagraphStyle(g[0].FontKey, first.SizePt, g[0].Color, first.Tz);
        var box = g[0].Bounds;
        foreach (var l in g.Skip(1)) box = box.Union(l.Bounds);
        var align = g.Count == 1 ? Alignment.Left : Classify(g, opt) ?? Alignment.Left;
        float indent = 0;
        if (g.Count >= 2 && align is Alignment.Left or Alignment.Justified)
        {
            double lmain = Geo.Median(g.Skip(1).Select(x => x.U0));
            indent = (float)Math.Max(0, g[0].U0 - lmain);
            if (indent < opt.AlignTolEm * g[0].Size) indent = 0;
        }
        float leading = g.Count >= 2 ? (float)Geo.Median(Pitches(g)) : (float)(g[0].Size * 1.2);   // C18
        return new ParagraphModel([.. g], box, style, align, indent, leading, (float)GrowthLimit(g, all, obs, opt), conf, reason);
    }

    /// <summary>문단 아래에서 처음 만나는 장애물·다른 줄의 위쪽 끝(로컬 v). 없으면 -∞. 문단을 통째로 덮은 배경은 제외.</summary>
    private static double GrowthLimit(List<TextLine> g, IReadOnlyList<TextLine> all, IReadOnlyList<Obstacle> obs, ParagraphOptions opt)
    {
        int angle = g[0].Angle;
        var last = g[^1];
        double u0 = g.Min(l => l.U0), u1 = g.Max(l => l.U1);
        double top = g[0].Baseline + 0.8 * g[0].Size;
        double limit = last.Baseline - opt.GrowthIgnoreBelowEm * last.Size;
        double best = double.NegativeInfinity;
        void Consider(double ou0, double ou1, double ov0, double ov1)
        {
            if (ou1 < u0 || ou0 > u1) return;                              // 가로로 안 겹침
            if (ou0 <= u0 && ou1 >= u1 && ov0 <= last.Baseline && ov1 >= top) return;   // 문단을 덮은 배경
            if (ov1 >= limit) return;                                       // 마지막 줄 바로 아래 아님
            best = Math.Max(best, ov1);
        }
        foreach (var o in obs)
        {
            var (a0, a1, b0, b1) = Geo.Project(o.Bounds, angle);
            Consider(a0, a1, b0, b1);
        }
        foreach (var l in all)
        {
            if (l.Angle != angle || g.Contains(l)) continue;
            Consider(l.U0, l.U1, l.Baseline - 0.25 * l.Size, l.Baseline + 0.8 * l.Size);
        }
        return best;
    }
}
