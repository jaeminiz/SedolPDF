using System.Text;
using Pdf.Native;
using PdfEditor.TestFixtures;

namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// M5 Task 1: 기술 검증 V17~V21. 탐침 구현(ProbeCore·ProbeFonts)과 판정 시험. 수치는 임시 폴더 m5-v*.txt에 쓰고
/// docs/validation/2026-10-m5-results.md에 옮긴다. 실제 PDF(private/)는 파일명·내용을 적지 않고 수치만 쓴다.
/// </summary>
public sealed partial class V17ToV21Probes : IDisposable
{
    private readonly PdfiumThread _t = new();
    public void Dispose() => _t.Dispose();

    internal static string Out(string name) => Path.Combine(Path.GetTempPath(), name);

    internal static string Norm(string s) => new(s.Where(c => !char.IsWhiteSpace(c) && c != '\u00a0').ToArray());

    private static string SampleDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "m5probe-samples");
        return d;
    }

    private static IReadOnlyList<ParagraphSample>? _samples;
    private static IReadOnlyList<ParagraphSample> Samples() => _samples ??= ParagraphPdfFactory.CreateAll(SampleDir());

    // ------------------------------------------------------------------ V17 (합성)
    internal sealed record Eval(ParagraphSample Sample, ParagraphProbe.Page Page, Dictionary<Seg, TruthLine> Match, int UnmatchedSegs, int UnmatchedTruth, PageData Data);

    internal Eval Prepare(ParagraphSample s)
    {
        var pd = ParagraphProbe.ReadPdfium(_t, s.Path, 0);
        var segs = ParagraphProbe.Assemble(pd.Runs, pd.Obstacles);
        var page = new ParagraphProbe.Page(segs, pd.Obstacles);
        var match = new Dictionary<Seg, TruthLine>();
        var used = new HashSet<TruthLine>();
        foreach (var seg in segs)
        {
            var tl = s.Truth.FirstOrDefault(x => !used.Contains(x) && Norm(x.Text) == Norm(seg.Text));
            if (tl is null) continue;
            used.Add(tl); match[seg] = tl;
        }
        return new Eval(s, page, match, segs.Count - match.Count, s.Truth.Count - used.Count, pd);
    }

    internal sealed record Score(int Groups, int Exact, int Partial, int Wrong, int TableMerge, int TruthMulti, int TruthExact, int Unknown);

    internal static Score Evaluate(Eval e, double threshold, Dictionary<Seg, Group>? cache = null)
    {
        var groups = ParagraphProbe.Partition(e.Page, threshold, cache);
        int exact = 0, partial = 0, wrong = 0, table = 0, unknown = 0, multi = 0;
        var truthCount = e.Sample.Truth.GroupBy(t => t.ParagraphId).ToDictionary(g => g.Key, g => g.Count());
        var exactPids = new HashSet<int>();
        foreach (var g in groups.Where(g => g.Lines.Count >= 2))
        {
            multi++;
            var tl = g.Lines.Select(l => e.Match.GetValueOrDefault(l)).ToList();
            if (tl.Any(x => x is null)) { unknown++; continue; }
            var pids = tl.Select(x => x!.ParagraphId).Distinct().ToList();
            if (pids.Count == 1)
            {
                if (truthCount[pids[0]] == g.Lines.Count) { exact++; exactPids.Add(pids[0]); } else partial++;
            }
            else { wrong++; if (tl.Any(x => x!.InTable)) table++; }
        }
        int truthMulti = truthCount.Count(kv => kv.Value >= 2);
        return new Score(multi, exact, partial, wrong, table, truthMulti, exactPids.Count, unknown);
    }

    private static readonly double[] Thresholds = [0.0, 0.5, 0.55, 0.6, 0.62, 0.65, 0.7, 0.75, 0.8, 0.85, 0.9];

    [Fact]
    public void V17_Synthetic_PrecisionAndTableMerge()
    {
        var sb = new StringBuilder();
        var evals = Samples().Select(Prepare).ToList();
        sb.AppendLine("== V17 합성 샘플: 줄 조립 일치 + 문턱별 문단 추정 결과 (그룹 = 2줄 이상으로 묶은 것)");
        foreach (var e in evals)
            sb.AppendLine($"{e.Sample.Name,-24} runs={e.Data.Runs.Count} segs={e.Page.Segs.Count} truthLines={e.Sample.Truth.Count} unmatchedSegs={e.UnmatchedSegs} unmatchedTruth={e.UnmatchedTruth} hidden={e.Data.Hidden} forms={e.Data.Forms}");
        foreach (var e in evals)
        {
            if (e.UnmatchedSegs == 0 && e.UnmatchedTruth == 0) continue;
            var segsU = e.Page.Segs.Where(s => !e.Match.ContainsKey(s)).Take(5).Select(s => "seg: " + s);
            var usedT = new HashSet<TruthLine>(e.Match.Values);
            var truthU = e.Sample.Truth.Where(x => !usedT.Contains(x)).Take(5).Select(x => "truth: " + x.Text);
            sb.AppendLine("unmatched in " + e.Sample.Name + ":"); foreach (var l in segsU.Concat(truthU)) sb.AppendLine("     " + l);
        }
        sb.AppendLine();
        foreach (var kind in new[] { "Document", "Drawing" })
        {
            sb.AppendLine($"-- {kind}: 문턱 T별 합계");
            sb.AppendLine("T      groups exact partial wrong tableMerge | truthMulti truthExact(recall) | precision(exact/groups)");
            foreach (double T in Thresholds)
            {
                var sc = evals.Where(e => e.Sample.Kind == kind).Select(e => Evaluate(e, T)).ToList();
                int g = sc.Sum(x => x.Groups), ex = sc.Sum(x => x.Exact), pa = sc.Sum(x => x.Partial), wr = sc.Sum(x => x.Wrong), tm = sc.Sum(x => x.TableMerge), tM = sc.Sum(x => x.TruthMulti), tE = sc.Sum(x => x.TruthExact);
                sb.AppendLine($"{T:0.00}   {g,6} {ex,5} {pa,7} {wr,5} {tm,10} | {tM,10} {tE,5} ({(tM == 0 ? 0 : 100.0 * tE / tM):0.#}%) | {(g == 0 ? 100 : 100.0 * ex / g):0.#}%");
            }
            sb.AppendLine();
        }
        sb.AppendLine("-- 샘플별 (T=0.0: 문턱 없음 / T=0.7)");
        foreach (var e in evals)
        {
            var a = Evaluate(e, 0.0); var b = Evaluate(e, 0.7);
            sb.AppendLine($"{e.Sample.Name,-24} T0: g{a.Groups} ex{a.Exact} pa{a.Partial} wr{a.Wrong} tbl{a.TableMerge} (truth {a.TruthExact}/{a.TruthMulti}) | T.7: g{b.Groups} ex{b.Exact} pa{b.Partial} wr{b.Wrong} tbl{b.TableMerge} (truth {b.TruthExact}/{b.TruthMulti})");
        }
        sb.AppendLine();
        sb.AppendLine("-- T=0.0에서도 놓친 문단(정답 문단 중 정확히 묶이지 않은 것) 줄 목록");
        foreach (var e in evals)
        {
            var gs = ParagraphProbe.Partition(e.Page, 0.0);
            foreach (var pid in e.Sample.Truth.GroupBy(t => t.ParagraphId).Where(g => g.Count() >= 2).Select(g => g.Key))
            {
                var tl = e.Sample.Truth.Where(t => t.ParagraphId == pid).ToList();
                bool ok = gs.Any(g => g.Lines.Count == tl.Count && g.Lines.All(l => e.Match.TryGetValue(l, out var t) && t.ParagraphId == pid));
                if (ok) continue;
                sb.AppendLine(e.Sample.Name + " pid=" + pid + " (" + tl.Count + "줄)");
                foreach (var t in tl) { var seg = e.Match.FirstOrDefault(kv => kv.Value == t).Key; var gg = seg is null ? null : gs.First(g => g.Lines.Contains(seg)); sb.AppendLine("     " + t.Text[..Math.Min(50, t.Text.Length)] + "  => group of " + (gg?.Lines.Count.ToString() ?? "?") + (seg is { TableRow: true } ? " TABLEROW" : "") + (seg is { Mixed: true } ? " MIXED" : "")); }
            }
        }
        sb.AppendLine();
        sb.AppendLine("-- 문턱 0.7에서 틀린 묶음 상세");
        foreach (var e in evals)
        {
            foreach (var g in ParagraphProbe.Partition(e.Page, 0.7).Where(g => g.Lines.Count >= 2))
            {
                var tl = g.Lines.Select(l => e.Match.GetValueOrDefault(l)).ToList();
                var pids = tl.Where(x => x is not null).Select(x => x!.ParagraphId).Distinct().ToList();
                bool exact = tl.All(x => x is not null) && pids.Count == 1 && e.Sample.Truth.Count(x => x.ParagraphId == pids[0]) == g.Lines.Count;
                if (exact) continue;
                sb.AppendLine($"{e.Sample.Name}: conf={g.Confidence:0.00} {g.Reason}");
                foreach (var l in g.Lines) sb.AppendLine($"     pid={(e.Match.TryGetValue(l, out var t) ? t.ParagraphId.ToString() : "?")} {l.Text[..Math.Min(70, l.Text.Length)]}");
            }
        }
        sb.AppendLine("-- 그룹별 신뢰도(T=0.0, 2줄 이상 그룹)");
        foreach (var e in evals)
            foreach (var g in ParagraphProbe.Partition(e.Page, 0.0).Where(g => g.Lines.Count >= 2))
            {
                var tl = g.Lines.Select(l => e.Match.GetValueOrDefault(l)).ToList();
                var pids = tl.Where(x => x is not null).Select(x => x!.ParagraphId).Distinct().ToList();
                bool ok = tl.All(x => x is not null) && pids.Count == 1 && e.Sample.Truth.Count(x => x.ParagraphId == pids[0]) == g.Lines.Count;
                sb.AppendLine($"{e.Sample.Name,-22} {(ok ? "OK   " : "WRONG")} conf={g.Confidence:0.00} {g.Reason}");
            }
        sb.AppendLine("-- 놓친 문단의 이웃 줄 판정 이유(T=0.0)");
        foreach (var e in evals)
            foreach (var pid in e.Sample.Truth.GroupBy(t => t.ParagraphId).Where(g => g.Count() >= 2).Select(g => g.Key))
            {
                var tl = e.Sample.Truth.Where(t => t.ParagraphId == pid).ToList();
                var gs = ParagraphProbe.Partition(e.Page, 0.0);
                if (gs.Any(g => g.Lines.Count == tl.Count && g.Lines.All(l => e.Match.TryGetValue(l, out var t) && t.ParagraphId == pid))) continue;
                var first = e.Match.FirstOrDefault(kv => kv.Value == tl[0]).Key; if (first is null) continue;
                var nb = ParagraphProbe.Neighbor(e.Page, first, true);
                sb.AppendLine(e.Sample.Name + " pid=" + pid + ": line1 " + first + " -> neighbor " + (nb is null ? "NONE (tie/none)" : nb + " why=" + (ParagraphProbe.WhyNot(e.Page, [first], nb, true) ?? "ok")));
            }
        File.WriteAllText(Out("m5-v17-synthetic.txt"), sb.ToString());
        // V17 합격 조건(합성): 문턱 Tc4에서 표 병합 오탐 0, 문서형 정밀도 ≥ 95 %, 도면형 잘못 묶음 0
        var atT = evals.Select(e => (e.Sample.Kind, Sc: Evaluate(e, Tc4))).ToList();
        Assert.Equal(0, atT.Sum(x => x.Sc.TableMerge));
        Assert.Equal(0, atT.Sum(x => x.Sc.Wrong));
        var docs = atT.Where(x => x.Kind == "Document").ToList();
        Assert.True(docs.Sum(x => x.Sc.Exact) >= 0.95 * docs.Sum(x => x.Sc.Groups), "문서형 정밀도 < 95 %");
        // 특수 샘플 단정
        var form = evals.First(e => e.Sample.Name == "dwg-form-text");
        Assert.Single(form.Data.Runs);                       // 폼 안 글은 쪽 글 객체가 아니다(V15)
        Assert.Equal(1, form.Data.Forms);
        var ocr = evals.First(e => e.Sample.Name == "dwg-invisible-ocr");
        Assert.Empty(ocr.Data.Runs);                         // 보이지 않는 글은 편집 대상 아님
        Assert.Equal(8, ocr.Data.Hidden);
    }

    // ------------------------------------------------------------------ 실제 PDF + 합성: 연구 묶음
    internal sealed record Source(string Label, string Kind, string Path, bool Real);

    internal static List<Source> RealSources()
    {
        string? priv = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "private"))) { priv = Path.Combine(d.FullName, "private"); break; }
        var list = new List<Source>();
        if (priv is null) return list;
        int doc = 0, dwg = 0;
        foreach (var f in Directory.GetFiles(priv, "*.pdf").OrderBy(x => x, StringComparer.Ordinal))
        {
            string n = System.IO.Path.GetFileName(f).ToLowerInvariant();
            bool isDoc = n.Contains("manual") || n.Contains("교육자료");
            list.Add(isDoc ? new Source($"real-doc-{++doc}", "Document", f, true) : new Source($"real-dwg-{++dwg}", "Drawing", f, true));
        }
        return list;
    }

    private const int RealPages = 5;
    private const double Tc4 = 0.62;      // C4 문턱: 합성 문턱 구간 0.60~0.65가 정밀도 100 %·표 병합 0, 실제 도면 목록 오탐 최대 신뢰도가 0.59 → 그 사이

    private List<PageStudy> StudyReal(Source s)
    {
        var result = new List<PageStudy>();
        try { result = ProbeStudy.Study(_t, s.Label, s.Path, RealPages); }
        catch (Exception e) { File.AppendAllText(Out("m5-errors.txt"), $"{s.Label}: {e.GetType().Name} {e.Message}\n"); }
        return result;
    }

    [Fact]
    public void V17_Real_Reproduction_And_Counts()
    {
        var sb = new StringBuilder();
        var books = new ProbeStudy.Books();
        sb.AppendLine("== V17 실제 PDF(앞 " + RealPages + "쪽): 문단 후보 수 + 무편집 재배치 재현율");
        var all = new List<(Source S, PageStudy P)>();
        foreach (var s in RealSources()) foreach (var p in StudyReal(s)) all.Add((s, p));
        int docMeas = 0, docOk = 0;
        foreach (var grp in all.GroupBy(x => x.S.Label))
        {
            var ps = grp.Select(x => x.P).ToList();
            sb.AppendLine($"-- {grp.Key} ({grp.First().S.Kind}): pages={ps.Count} textObjs={ps.Sum(p => p.Pd.TextObjects)} runs={ps.Sum(p => p.Pd.Runs.Count)} hidden={ps.Sum(p => p.Pd.Hidden)} badAngle={ps.Sum(p => p.Pd.BadAngle)} forms={ps.Sum(p => p.Pd.Forms)} paths={ps.Sum(p => p.Pd.Paths)} obstacles={ps.Sum(p => p.Pd.Obstacles.Count)}");
            sb.AppendLine($"   segs={ps.Sum(p => p.Page.Segs.Count)} mixed={ps.Sum(p => p.Page.Segs.Count(s => s.Mixed))} tableRow={ps.Sum(p => p.Page.Segs.Count(s => s.TableRow))} streamMapOk={ps.Count(p => p.Streams is not null)}/{ps.Count} fails=[{string.Join("; ", ps.Where(p => p.StreamFail is not null).Select(p => (p.StreamFail ?? "").Length > 60 ? (p.StreamFail ?? "")[..60] : (p.StreamFail ?? "")))}]");
            foreach (double T in new[] { 0.5, 0.65, 0.8 })
            {
                int g2 = 0, lines = 0, segs = 0;
                foreach (var p in ps) { var gs = ParagraphProbe.Partition(p.Page, T); g2 += gs.Count(g => g.Lines.Count >= 2); lines += gs.Where(g => g.Lines.Count >= 2).Sum(g => g.Lines.Count); segs += p.Page.Segs.Count; }
                sb.AppendLine($"   T={T:0.00}: multi-line groups={g2} lines in groups={lines}/{segs} ({(segs == 0 ? 0 : 100.0 * lines / segs):0.#}%)");
            }
        }
        sb.AppendLine();
        sb.AppendLine("-- 무편집 재배치 재현 (T=" + Tc4 + " 이상의 2줄 이상 그룹)");
        foreach (var kind in new[] { "Document", "Drawing" })
            foreach (bool relaxed in new[] { false, true })
            {
                int groups = 0, measurable = 0, word = 0, chr = 0, either = 0;
                var byConf = new Dictionary<string, (int n, int ok)>();
                var whyHist = new Dictionary<string, int>();
                foreach (var (s, p) in all.Where(x => x.S.Kind == kind))
                    foreach (var g in ParagraphProbe.Partition(p.Page, Tc4).Where(g => g.Lines.Count >= 2))
                    {
                        groups++;
                        var r = ProbeStudy.Reproduce(p, g, books, relaxed);
                        if (!r.Measurable) { string k = r.Why.Length > 38 ? r.Why[..38] : r.Why; if (k.StartsWith("width unknown")) k = "width unknown"; whyHist[k] = whyHist.GetValueOrDefault(k) + 1; continue; }
                        measurable++;
                        if (r.WordOk) word++;
                        if (r.CharOk) chr++;
                        bool ok = r.WordOk || r.CharOk;
                        if (!ok && !relaxed) sb.AppendLine($"     [재현 실패 {s.Label} p{p.PageIndex + 1}] n={g.Lines.Count} conf={g.Confidence:0.00} {g.Align} {r.Why}");
                        if (ok) either++;
                        string b = g.Confidence >= 0.9 ? ">=0.90" : g.Confidence >= 0.8 ? "0.80-0.89" : g.Confidence >= 0.7 ? "0.70-0.79" : "0.65-0.69";
                        var cur = byConf.GetValueOrDefault(b);
                        byConf[b] = (cur.n + 1, cur.ok + (ok ? 1 : 0));
                    }
                if (kind == "Document" && !relaxed) { docMeas = measurable; docOk = either; }
                sb.AppendLine($"{kind} {(relaxed ? "[완화: WinAnsi 단순 글꼴 포함]" : "[엄격: C8 규칙]")}: groups={groups} measurable={measurable} ({(groups == 0 ? 0 : 100.0 * measurable / groups):0.#}%) wordWrapOk={word} charWrapOk={chr} either={either} => recall(measurable)={(measurable == 0 ? 0 : 100.0 * either / measurable):0.#}% recall(all groups)={(groups == 0 ? 0 : 100.0 * either / groups):0.#}%");
                sb.AppendLine("     not measurable: " + string.Join(", ", whyHist.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
                sb.AppendLine("     by confidence: " + string.Join(", ", byConf.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value.ok}/{kv.Value.n}")));
            }
        File.WriteAllText(Out("m5-v17-real.txt"), sb.ToString());
        if (docMeas > 0) Assert.True(docOk >= 0.7 * docMeas, $"실제 문서형 무편집 재현율 {docOk}/{docMeas} < 70 %");
    }

    private List<PageStudy> StudySynthetic()
    {
        var all = new List<PageStudy>();
        foreach (var s in Samples()) all.AddRange(ProbeStudy.Study(_t, "syn:" + s.Name, s.Path, 1));
        return all;
    }

    [Fact]
    public void V17_Synthetic_Reproduction_Sanity()
    {
        var sb = new StringBuilder();
        var books = new ProbeStudy.Books();
        sb.AppendLine("== V17 합성 샘플에서 같은 재배치 알고리즘(무편집 재현율 — 알고리즘 자체의 점검, 줄바꿈은 PDFsharp 측정폭으로 만든 것)");
        int groups = 0, meas = 0, ok = 0;
        foreach (var ps in StudySynthetic().Where(p => p.Label.StartsWith("syn:doc") || p.Label.StartsWith("syn:dwg-notes") || p.Label.StartsWith("syn:dwg-rot")))
            foreach (var g in ParagraphProbe.Partition(ps.Page, Tc4).Where(g => g.Lines.Count >= 2))
            {
                groups++;
                var r = ProbeStudy.Reproduce(ps, g, books, relaxed: false);
                if (!r.Measurable) { sb.AppendLine($"  {ps.Label}: not measurable ({r.Why})"); continue; }
                meas++;
                bool good = r.WordOk || r.CharOk;
                if (good) ok++; else sb.AppendLine($"  {ps.Label}: NOT reproduced: {r.Why} first line: {g.Lines[0].Text[..Math.Min(30, g.Lines[0].Text.Length)]}");
            }
        sb.AppendLine($"groups={groups} measurable={meas} reproduced={ok} recall(measurable)={(meas == 0 ? 0 : 100.0 * ok / meas):0.#}%");
        File.WriteAllText(Out("m5-v17-synth-repro.txt"), sb.ToString());
    }
    // ------------------------------------------------------------------ V19 한글 줄 이음
    [Fact]
    public void V19_KoreanLineJoin()
    {
        var sb = new StringBuilder();
        var books = new ProbeStudy.Books();
        sb.AppendLine("== V19 한글 줄 이음: 줄 끝 여유 slack = (같은 열의 가장 먼 줄 끝 − 이 줄 자연 끝) / 글자 크기. 어절 단위 줄바꿈이면 여유가 1.1 em을 넘는 줄이 많고, 글자 단위 줄바꿈이면 거의 없다(낱말 길이가 아니라 글자 하나 폭 안).");
        var studies = StudySynthetic().Where(p => p.Label.StartsWith("syn:doc-ko")).ToList();
        foreach (var s in RealSources()) studies.AddRange(StudyReal(s));
        foreach (var grp in studies.GroupBy(p => p.Label))
        {
            var pooled = new List<double>(); int korGroups = 0, wordOk = 0, charOk = 0;
            foreach (var ps in grp)
            {
                var items = new List<(double U0, double Nat, double Em, bool Last)>();
                foreach (var g in ParagraphProbe.Partition(ps.Page, Tc4).Where(g => g.Lines.Count >= 2))
                {
                    var r = ProbeStudy.Reproduce(ps, g, books, relaxed: false);
                    if (!r.Measurable || !r.Korean || r.Lines is null) continue;
                    korGroups++;
                    if (r.WordOk) wordOk++;
                    if (r.CharOk) charOk++;
                    items.AddRange(r.Lines);
                }
                foreach (var col in items.GroupBy(x => Math.Round(x.U0 / (3 * x.Em))))
                {
                    double R = col.Max(x => x.U0 + x.Nat);
                    pooled.AddRange(col.Where(x => !x.Last).Select(x => (R - (x.U0 + x.Nat)) / x.Em));
                }
            }
            if (korGroups == 0) { sb.AppendLine($"{grp.Key}: 한글 문단 후보 없음"); continue; }
            double pBig = pooled.Count(x => x > 1.1) / (double)Math.Max(1, pooled.Count);
            sb.AppendLine($"{grp.Key}: 한글 문단 {korGroups}개, 비마지막 줄 {pooled.Count}개, 여유>1.1em 비율={100 * pBig:0}%, 중앙값={ParagraphProbe.Median(pooled):0.00}em, 최대={(pooled.Count == 0 ? 0 : pooled.Max()):0.0}em | 낱말단위 재현 {wordOk}/{korGroups}, 글자단위 재현 {charOk}/{korGroups} => 판정: {(pooled.Count < 6 ? "표본 부족" : pBig >= 0.3 ? "어절 단위" : pBig <= 0.15 ? "글자 단위(낱말 중간에서도 끊김)" : "불명")}");
        }
        File.WriteAllText(Out("m5-v19.txt"), sb.ToString());
    }

    // ------------------------------------------------------------------ V18 코드북 적용 범위
    /// <summary>글자 방향 이동량(앞뒤 공백 코드는 뺀 잉크 쪽 폭 근사).</summary>
    private static double AdvanceTrim(Book book, TextStream ts, double scale)
    {
        var items = new List<(bool Code, int C, double Adv)>();
        foreach (var (bytes, adj) in ts.Pieces)
        {
            if (bytes is null) { items.Add((false, 0, -adj / 1000.0 * ts.Tf)); continue; }
            foreach (int code in book.Codes(bytes))
                items.Add((true, code, book.Width(code) / 1000.0 * ts.Tf + ts.Tc + (!book.TwoByte && code == 32 ? ts.Tw : 0)));
        }
        bool IsSpace(int code) => (book.ToUni.TryGetValue(code, out var s) ? s : book.Relaxed && code < 256 ? ((char)code).ToString() : "?").Trim().Length == 0;
        int first = items.FindIndex(x => x.Code && !IsSpace(x.C)), last = items.FindLastIndex(x => x.Code && !IsSpace(x.C));
        if (first < 0) return 0;
        double adv = 0;
        for (int i = first; i <= last; i++) adv += items[i].Adv;
        return adv * ts.Tz / 100.0 * scale;
    }

    private sealed class KindStat
    {
        public int Runs, StrictOk, RelaxedOk, TextAgree, W1, W2, WBad, Compared, LearnedOk, LearnedBad, LearnedConflict, LW1, LW2, LWBad;
        public Dictionary<(PdfSharp.Pdf.PdfDictionary, int), char> Learned = new();
        public double SignedErrEmSum;
        public Dictionary<string, int> Reasons = new();
    }

    [Fact]
    public void V18_CodebookCoverage_And_WidthAccuracy()
    {
        var sb = new StringBuilder();
        var books = new ProbeStudy.Books();
        var sets = new List<(string Name, List<PageStudy> Pages)>
        {
            ("synthetic", StudySynthetic()),
        };
        foreach (var kind in new[] { "Document", "Drawing" })
        {
            var l = new List<PageStudy>();
            foreach (var s in RealSources().Where(x => x.Kind == kind)) l.AddRange(StudyReal(s));
            sets.Add(("real-" + kind.ToLowerInvariant(), l));
        }
        sb.AppendLine("== V18 코드북(원래 글꼴 직접 인코딩) 적용 범위: 글 객체(run) 단위, 보이는 글만");
        foreach (var (name, pages) in sets)
        {
            int total = pages.Sum(p => p.Pd.Runs.Count), noMap = pages.Where(p => p.Streams is null).Sum(p => p.Pd.Runs.Count);
            int noFont = pages.Where(p => p.Streams is not null).Sum(p => p.Pd.Runs.Count(r => !p.RunStream.ContainsKey(r)));
            var stats = new Dictionary<string, KindStat>();
            foreach (var p in pages)
                foreach (var (run, ts) in p.RunStream)
                {
                    string kind = BookBuilder.KindOf(ts.Font);
                    var (strict, why) = books.Get(ts.Font, false);
                    var (relaxed, _) = books.Get(ts.Font, true);
                    if (!stats.TryGetValue(kind, out var st)) stats[kind] = st = new KindStat();
                    st.Runs++;
                    if (relaxed is not null) st.RelaxedOk++;
                    if (strict is null && why == "no ToUnicode")
                    {
                        var (nb, _) = BookBuilder.Build(ts.Font, false, true);
                        var codes = nb is null ? [] : ts.Pieces.Where(x => x.Bytes is not null).SelectMany(x => nb.Codes(x.Bytes!)).ToList();
                        string text = run.Text.Trim();
                        var nonSpace = codes.Where(c => c != 32).ToList();
                        string tns = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
                        if (nb is not null && nonSpace.Count == tns.Length && tns.Length > 0)
                        {
                            st.LearnedOk++;
                            { double ladv = AdvanceTrim(nb, ts, run.Scale), lw = run.U1 - run.U0, lerr = Math.Abs(ladv - lw), ltol = Math.Max(1.0, 0.01 * lw); if (lerr <= ltol) st.LW1++; else if (lerr <= ltol + 0.15 * run.Size) st.LW2++; else st.LWBad++; }
                            for (int i = 0; i < nonSpace.Count; i++)
                                if (st.Learned.TryGetValue((ts.Font, nonSpace[i]), out char prev) && prev != tns[i]) st.LearnedConflict++; else st.Learned[(ts.Font, nonSpace[i])] = tns[i];
                        }
                        else st.LearnedBad++;
                    }
                    if (strict is null) { st.Reasons[why.Length > 36 ? why[..36] : why] = st.Reasons.GetValueOrDefault(why.Length > 36 ? why[..36] : why) + 1; continue; }
                    st.StrictOk++;
                    string dec = strict.Decode(ts.Pieces.Where(x => x.Bytes is not null).SelectMany(x => x.Bytes!).ToArray());
                    if (Norm(dec) == Norm(run.Text)) st.TextAgree++;
                    double adv = AdvanceTrim(strict, ts, run.Scale), wgeo = run.U1 - run.U0;
                    if (wgeo > 0.5)
                    {
                        st.Compared++;
                        double err = Math.Abs(adv - wgeo), tol1 = Math.Max(1.0, 0.01 * wgeo);
                        st.SignedErrEmSum += (adv - wgeo) / run.Size;
                        if (err <= tol1) st.W1++; else if (err <= tol1 + 0.15 * run.Size) st.W2++; else st.WBad++;
                    }
                }
            sb.AppendLine($"-- {name}: runs={total} (스트림 대응 실패 쪽의 글 {noMap}, 글꼴 사전 못 찾음 {noFont}) 글꼴 사전 해석된 글 {stats.Values.Sum(s => s.Runs)}");
            foreach (var (k, st) in stats.OrderByDescending(kv => kv.Value.Runs))
                sb.AppendLine($"   {k,-34} runs={st.Runs,5} 엄격(C8)={st.StrictOk,5} ({100.0 * st.StrictOk / st.Runs:0}%) 완화={st.RelaxedOk,5} | 글자 일치={st.TextAgree}/{st.StrictOk} | 폭 오차 ≤1%또는1pt={st.W1} ≤+0.15em={st.W2} 초과={st.WBad} (비교 {st.Compared}, 부호 평균 {(st.Compared == 0 ? 0 : st.SignedErrEmSum / st.Compared):0.000}em) | ToUnicode 없음 → PDFium 글과 짝지어 배우기: 성공 {st.LearnedOk} 실패 {st.LearnedBad} 충돌 {st.LearnedConflict} (폭 오차 ≤1%또는1pt {st.LW1}, ≤+0.15em {st.LW2}, 초과 {st.LWBad}) | 실패 이유: {string.Join(", ", st.Reasons.Select(kv => kv.Key + "=" + kv.Value))}");
            int sr = stats.Values.Sum(s => s.Runs), ok = stats.Values.Sum(s => s.StrictOk), rl = stats.Values.Sum(s => s.RelaxedOk);
            sb.AppendLine($"   합계: 엄격 {ok}/{sr} ({(sr == 0 ? 0 : 100.0 * ok / sr):0.#}%), 완화 {rl}/{sr} ({(sr == 0 ? 0 : 100.0 * rl / sr):0.#}%)");
        }
        File.WriteAllText(Out("m5-v18.txt"), sb.ToString());
        var realDoc = sets.First(s => s.Name == "real-document").Pages;
        if (realDoc.Count > 0)
        {
            int rr = realDoc.Sum(p => p.RunStream.Count), okk = realDoc.Sum(p => p.RunStream.Count(kv => books.Get(kv.Value.Font, false).Book is not null));
            Assert.True(okk >= 0.9 * rr, "실제 문서형 코드북 적용 비율 < 90 %");
        }
    }
}
