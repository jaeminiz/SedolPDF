using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Pdf.Native;
using PdfEditor.ContentStream;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// 0.4.0 Task 1: 기술 검증 V22(찾기)·V23(바꾸기 충실도)과 실제 PDF 표본 수치. 탐침 구현은 FindProbe·DrawingIdPdfFactory(시험 안).
/// 수치는 임시 폴더 v040-*.txt에 쓰고 docs/validation/2026-10-v040-results.md에 옮긴다. 실제 PDF(private/)는 파일명·내용을 적지 않고 수치만 쓴다.
/// </summary>
public sealed class V22ToV27Probes : IDisposable
{
    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v040-probe");
    public V22ToV27Probes() => Directory.CreateDirectory(_dir);
    public void Dispose() => _t.Dispose();

    private string P(string n) => Path.Combine(_dir, n);
    internal static string Out(string name) => Path.Combine(Path.GetTempPath(), name);

    private static string? PrivateDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "private"))) return Path.Combine(d.FullName, "private");
        return null;
    }

    private int PdfiumCount(string path, int page, string q, bool mc, bool ww) =>
        _t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(_t, path, null);
            using var p = d.LoadPage(page);
            using var tp = p.LoadText();
            return tp.FindAll(q, mc, ww).Count;
        }).GetAwaiter().GetResult();

    private byte[] Render(string path, double scale = 2, int pageIndex = 0) =>
        _t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(_t, path, null);
            using var p = d.LoadPage(pageIndex);
            var sz = p.SizePt;
            int w = (int)Math.Ceiling(sz.Width * scale), h = (int)Math.Ceiling(sz.Height * scale);
            return p.RenderTile(scale, 0, 0, w, h, withAnnotations: false);
        }).GetAwaiter().GetResult();

    private static string LastBox = "";

    private static (int DiffPx, int InkPx, double Pct) Compare(byte[] a, byte[] b, int widthPx = 1190)
    {
        int ink = 0, diff = 0, bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
        for (int i = 0; i + 3 < a.Length && i + 3 < b.Length; i += 4)
        {
            bool ia = a[i] < 200 || a[i + 1] < 200 || a[i + 2] < 200, ib = b[i] < 200 || b[i + 1] < 200 || b[i + 2] < 200;
            if (!ia && !ib) continue;
            ink++;
            if (Math.Abs(a[i] - b[i]) > 64 || Math.Abs(a[i + 1] - b[i + 1]) > 64 || Math.Abs(a[i + 2] - b[i + 2]) > 64)
            {
                diff++;
                int px = i / 4, x = px % widthPx, y = px / widthPx;
                bx0 = Math.Min(bx0, x); by0 = Math.Min(by0, y); bx1 = Math.Max(bx1, x); by1 = Math.Max(by1, y);
            }
        }
        LastBox = diff == 0 ? "-" : $"box=({bx0 / 2},{by0 / 2})-({bx1 / 2},{by1 / 2})pt";
        return (diff, ink, ink == 0 ? 0 : 100.0 * diff / ink);
    }

    private static (WalkResult W, List<PLine> Lines) Analyze(string path, int pageIndex = 0)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var w = FindProbe.Walk(doc.Pages[pageIndex]);
        return (w, FindProbe.BuildLines(w.Glyphs));
    }

    private static readonly (string Name, bool Case, bool Whole, int Truth)[] Modes =
    [
        ("case+whole", true, true, 3),
        ("nocase+substring", false, false, 5),
        ("nocase+whole", false, true, 4),
    ];

    // ------------------------------------------------------------------ V22
    [Fact]
    public void V22_Find_RecallAndFalsePositives_OnSyntheticCases()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== V22 합성 표본 찾기: 모드별 (찾은 수 / 정답) · 구간 되돌리기 성공 · PDFium FindAll 수 · 쪽 해석+찾기 시간");
        foreach (var c in Enum.GetValues<IdCase>())
        {
            string path = P($"v22-{c}.pdf");
            DrawingIdPdfFactory.Create(path, c);
            var sw = Stopwatch.StartNew();
            var (w, lines) = Analyze(path);
            var results = Modes.Select(m => (m, Matches: FindProbe.Find(lines, DrawingIdPdfFactory.Id, m.Case, m.Whole))).ToList();
            double ms = sw.Elapsed.TotalMilliseconds;
            sb.Append($"{c,-12}: glyphs={w.Glyphs.Count} lines={lines.Count} unsearchableOps={w.UnsearchableOps}/{w.ShowOps}  ");
            foreach (var (m, ms2) in results)
            {
                int pd = PdfiumCount(path, 0, DrawingIdPdfFactory.Id, m.Case, m.Whole);
                int back = ms2.Count(x => FindProbe.RangesDecodeBack(w, x));
                sb.Append($"[{m.Name}: {ms2.Count}/{m.Truth} back={back} pdfium={pd}] ");
            }
            sb.AppendLine($" {ms:0.0} ms");
            if (c == IdCase.NoToUnicode)
            {
                Assert.All(results, r => Assert.Empty(r.Matches));
                Assert.True(w.UnsearchableOps > 0);
            }
            else
            {
                foreach (var (m, ms2) in results)
                {
                    Assert.Equal(m.Truth, ms2.Count);
                    Assert.All(ms2, x => Assert.True(FindProbe.RangesDecodeBack(w, x), $"{c} {m.Name}"));
                }
            }
        }
        File.WriteAllText(Out("v040-v22-synthetic.txt"), sb.ToString());
    }

    // ------------------------------------------------------------------ V23
    [Fact]
    public void V23_Replace_Fidelity_OnSyntheticCases()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== V23 바꾸기 충실도: 편집본 vs 새 번호로 처음부터 만든 기준본(쪽 전체 비교) · 옛 번호 잔존(PDFium/내용 해석) · 새 번호 개수(PDFium)");
        int fail = 0;
        foreach (var c in Enum.GetValues<IdCase>().Where(x => x != IdCase.NoToUnicode))
        {
            foreach (var newId in new[] { "H-1052", "H-26", "H-250123" })
            {
                string src = P($"v23-{c}-src.pdf"), edit = P($"v23-{c}-{newId}-edit.pdf"), reference = P($"v23-{c}-{newId}-ref.pdf");
                DrawingIdPdfFactory.Create(src, c);
                DrawingIdPdfFactory.Create(reference, DrawingIdPdfFactory.RefCaseOf(c), newId, refLayout: true);
                var (w, lines) = Analyze(src);
                var ms = FindProbe.Find(lines, DrawingIdPdfFactory.Id, true, true);
                var r = FindProbe.Replace(w, lines, ms, newId);
                if (r.Data is null) { sb.AppendLine($"{c,-12} {newId,-9}: 실패 {r.Notes} (needsApp={r.NeedsAppFont} unsup={r.Unsupported})"); fail++; continue; }
                using (var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify))
                {
                    var page = doc.Pages[0];
                    page.Contents.Elements.Clear();
                    page.Contents.AppendContent().CreateStream(r.Data);
                    doc.Save(edit);
                }
                var a = Render(edit); var b = Render(reference); var o = Render(src);
                int wpx = c == IdCase.PageRot90 ? 1684 : 1190;   // 배율 2, 가로폭(px): A4 595pt → 1190, 쪽 /Rotate 90이면 842pt → 1684
                bool rot = c == IdCase.PageRot90;
                Func<int, int, bool> inside = (px, py) =>
                {
                    foreach (var rg in r.Regions!)
                    {
                        double th = Math.Atan2(rg.Y1 - rg.Y0, rg.X1 - rg.X0), nx = -Math.Sin(th) * 1.3 * rg.Size, ny = Math.Cos(th) * 1.3 * rg.Size;
                        var xs = new[] { rg.X0 + nx, rg.X0 - nx, rg.X1 + nx, rg.X1 - nx }; var ys = new[] { rg.Y0 + ny, rg.Y0 - ny, rg.Y1 + ny, rg.Y1 - ny };
                        double bx0 = xs.Min() - 3, bx1 = xs.Max() + 3, by0 = ys.Min() - 3, by1 = ys.Max() + 3;   // 쪽 공간(pt) 경계 + 여유 3pt
                        double X = (rot ? py : px) / 2.0, Y = rot ? px / 2.0 : 842 - py / 2.0;   // 픽셀 → 쪽 공간
                        if (X >= bx0 && X <= bx1 && Y >= by0 && Y <= by1) return true;
                    }
                    return false;
                };
                int outsideSrc = 0, insideInk = 0, insideDiff = 0;
                for (int i = 0; i + 3 < a.Length; i += 4)
                {
                    int px = (i / 4) % wpx, py = (i / 4) / wpx;
                    bool in_ = inside(px, py);
                    bool dSrc = Math.Abs(a[i] - o[i]) > 64 || Math.Abs(a[i + 1] - o[i + 1]) > 64 || Math.Abs(a[i + 2] - o[i + 2]) > 64;
                    if (!in_ && dSrc) outsideSrc++;
                    if (in_)
                    {
                        bool ia = a[i] < 200 || a[i + 1] < 200 || a[i + 2] < 200, ib = b[i] < 200 || b[i + 1] < 200 || b[i + 2] < 200;
                        if (ia || ib) { insideInk++; if (Math.Abs(a[i] - b[i]) > 64 || Math.Abs(a[i + 1] - b[i + 1]) > 64 || Math.Abs(a[i + 2] - b[i + 2]) > 64) insideDiff++; }
                    }
                }
                var (dpx, ink, pct) = Compare(a, b, wpx); string boxRef = LastBox;
                var (opx, _, _) = Compare(o, a, wpx);
                int oldPdfium = PdfiumCount(edit, 0, DrawingIdPdfFactory.Id, true, true);
                int newPdfium = PdfiumCount(edit, 0, newId, true, true);
                var (w2, lines2) = Analyze(edit);
                int oldMine = FindProbe.Find(lines2, DrawingIdPdfFactory.Id, true, true).Count;
                int newMine = FindProbe.Find(lines2, newId, true, true).Count;
                sb.AppendLine($"{c,-12} {newId,-9}: replaced={r.Replaced}/3 needsApp={r.NeedsAppFont} overlapWarn={r.Overlaps} | OUTSIDE-regions diff vs source={outsideSrc}px | INSIDE-regions vs ref: diff={insideDiff}/{insideInk}ink ({(insideInk == 0 ? 0 : 100.0 * insideDiff / insideInk):0.00}%) | whole-page vs-ref diffPx={dpx} pct={pct:0.00}% {boxRef} | orig-vs-edit diffPx={opx} | old: pdfium={oldPdfium} mine={oldMine} | new: pdfium={newPdfium} mine={newMine}");
                bool longer = newId.Length > DrawingIdPdfFactory.Id.Length;   // 길어지면 문장 속 번호가 뒤 글을 덮는다(D8) → 겹침 경고가 떠야 한다
                if (r.Replaced != 3 || oldPdfium != 0 || outsideSrc != 0 || (longer ? r.Overlaps < 1 : r.Overlaps != 0 || oldMine != 0 || newMine != 3)) fail++;
            }
        }
        File.WriteAllText(Out("v040-v23-synthetic.txt"), sb.ToString());
        Assert.Equal(0, fail);
    }

    [Fact]
    public void V23_OriginalFontCoverage_WithAndWithoutLegend()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== V23 원래 글꼴로 새 번호를 쓸 수 있는가(코드북): 범례 있음/없음 × 새 번호");
        foreach (bool legend in new[] { true, false })
            foreach (var newId in new[] { "H-1052", "H-2604", "H-2501A", "H-3399" })
            {
                string src = P($"v23c-{legend}.pdf");
                DrawingIdPdfFactory.Create(src, IdCase.Whole, legend: legend);
                var (w, lines) = Analyze(src);
                var ms = FindProbe.Find(lines, DrawingIdPdfFactory.Id, true, true);
                var r = FindProbe.Replace(w, lines, ms, newId);
                sb.AppendLine($"legend={legend,-5} {newId,-8}: replaced={r.Replaced} needsAppFont={r.NeedsAppFont}");
            }
        File.WriteAllText(Out("v040-v23-coverage.txt"), sb.ToString());
    }

    // ------------------------------------------------------------------ 실제 PDF 수치(이름·내용 없음)
    [Fact]
    public void V22_RealPdfs_SearchableShare_Timing_PdfiumAgreement()
    {
        string? dir = PrivateDir();
        if (dir is null) return;
        var sb = new StringBuilder();
        sb.AppendLine("== V22 실제 PDF(앞 5쪽, 이름·내용 없음): 글 연산 수·찾기 가능 비율·시간·PDFium과 일치율");
        var files = Directory.GetFiles(dir, "*.pdf").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var tok = new Regex(@"\b[A-Z]{1,3}-?\d{3,5}\b", RegexOptions.Compiled);
        for (int fi = 0; fi < files.Count; fi++)
        {
            using var doc = PdfReader.Open(files[fi], PdfDocumentOpenMode.Import);
            int pages = Math.Min(5, doc.PageCount);
            int forms = 0, formTextOps = 0, showOps = 0, unsearch = 0, glyphs = 0, quote = 0, linesN = 0, type0NoBook = 0, failures = 0;
            double msTotal = 0, msMax = 0;
            var freq = new Dictionary<string, int>();
            var perPage = new List<(WalkResult W, List<PLine> L)>();
            for (int p = 0; p < pages; p++)
            {
                var sw = Stopwatch.StartNew();
                var w = FindProbe.Walk(doc.Pages[p]);
                var lines = FindProbe.BuildLines(w.Glyphs);
                double ms = sw.Elapsed.TotalMilliseconds;
                msTotal += ms; msMax = Math.Max(msMax, ms);
                if (w.Failure is not null) failures++;
                if (w.Pc is { Ok: true }) { var (fm, fo) = FormTextOps(doc.Pages[p], w.Pc); forms += fm; formTextOps += fo; }
                showOps += w.ShowOps; unsearch += w.UnsearchableOps; glyphs += w.Glyphs.Count; quote += w.QuoteOps; linesN += lines.Count;
                type0NoBook += w.Fonts.Values.Count(f => f.Type0 && f.Book is null);
                foreach (Match m in tok.Matches(PdfiumText(files[fi], p))) freq[m.Value] = freq.GetValueOrDefault(m.Value) + 1;
                perPage.Add((w, lines));
            }
            // 가장 많이 나온 번호꼴 상위 3개를 내 찾기와 PDFium으로 세어 비교
            var top = freq.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();
            var cmp = new List<string>();
            foreach (var t in top)
            {
                int mine = 0, pdf = 0, dups = 0, rotated = 0, textOcc = 0;
                for (int p = 0; p < pages; p++)
                {
                    var found = FindProbe.Find(perPage[p].L, t, false, false); mine += found.Count;
                    var cs = found.Select(m => (m, c: (X: (m.Glyphs[0].X0 + m.Glyphs[^1].X1) / 2, Y: (m.Glyphs[0].Y0 + m.Glyphs[^1].Y1) / 2))).ToList();
                    for (int a = 0; a < cs.Count; a++) for (int b = a + 1; b < cs.Count; b++) if (Math.Abs(cs[a].c.X - cs[b].c.X) < 0.15 * cs[a].m.Glyphs[0].Size && Math.Abs(cs[a].c.Y - cs[b].c.Y) < 0.15 * cs[a].m.Glyphs[0].Size) { dups++; break; }
                    rotated += found.Count(m => m.Glyphs[0].Angle != 0);
                    pdf += PdfiumPageCount(files[fi], p, t); textOcc += CountOcc(PdfiumText(files[fi], p), t);
                }
                cmp.Add($"{mine}/{pdf} (pdfiumTextSubstringCount={textOcc}, overprint-duplicates={dups}, rotated={rotated})");
            }
            int pdfChars = Enumerable.Range(0, pages).Sum(pg => PdfiumText(files[fi], pg).Length);
            sb.AppendLine($"real#{fi + 1}: pdfiumChars={pdfChars} formsCalled={forms} textOpsInsideForms={formTextOps} pages={pages} showOps={showOps} unsearchableOps={unsearch} ({(showOps == 0 ? 0 : 100.0 * unsearch / showOps):0.0}%) quoteOps={quote} glyphs={glyphs} lines={linesN} walkFailures={failures} type0NoToUnicodeFonts={type0NoBook} | time total={msTotal:0} ms max/page={msMax:0} ms | id-like tokens top3 (mine/pdfium)= {string.Join(", ", cmp)}");
        }
        File.WriteAllText(Out("v040-v22-real.txt"), sb.ToString());
    }

    [Fact]
    public void V23_RealPdfs_ReplaceMostFrequentIdLikeToken_WithItsReverse()
    {
        string? dir = PrivateDir();
        if (dir is null) return;
        var sb = new StringBuilder();
        sb.AppendLine("== V23 실제 PDF(앞 5쪽 중 번호꼴이 가장 많은 쪽, 이름·내용 없음): 가장 많이 나온 번호꼴을 글자 순서를 뒤집은 글로 바꿈 → 영역 밖 차이·옛 번호 잔존·원래 글꼴 가능 여부");
        var tok = new Regex(@"\b[A-Z]{1,3}-?\d{3,5}\b", RegexOptions.Compiled);
        var files = Directory.GetFiles(dir, "*.pdf").OrderBy(f => f, StringComparer.Ordinal).ToList();
        for (int fi = 0; fi < files.Count; fi++)
        {
            using var doc = PdfReader.Open(files[fi], PdfDocumentOpenMode.Modify);
            int pages = Math.Min(5, doc.PageCount);
            (int Page, string Token, int Count)? best = null;
            for (int p = 0; p < pages; p++)
            {
                if (doc.Pages[p].Rotate != 0) continue;
                var w0 = FindProbe.Walk(doc.Pages[p]);
                var l0 = FindProbe.BuildLines(w0.Glyphs);
                var freq = new Dictionary<string, int>();
                foreach (var l in l0) foreach (Match m in tok.Matches(l.Text)) freq[m.Value] = freq.GetValueOrDefault(m.Value) + 1;
                foreach (var kv in freq.Where(k => k.Value >= 2 && new string(k.Key.Reverse().ToArray()) != k.Key))
                    if (best is null || kv.Value > best.Value.Count) best = (p, kv.Key, kv.Value);
            }
            if (best is null) { sb.AppendLine($"real#{fi + 1}: 번호꼴 반복 없음(건너뜀)"); continue; }
            var (pg, token, count) = best.Value;
            string newText = new(token.Reverse().ToArray());
            var page = doc.Pages[pg];
            var w = FindProbe.Walk(page); var lines = FindProbe.BuildLines(w.Glyphs);
            var ms = FindProbe.Find(lines, token, true, false);
            var sw = Stopwatch.StartNew();
            var r = FindProbe.Replace(w, lines, ms, newText);
            double replMs = sw.Elapsed.TotalMilliseconds;
            if (r.Data is null) { sb.AppendLine($"real#{fi + 1}: matches={ms.Count} replaced={r.Replaced} needsAppFont={r.NeedsAppFont} unsupported={r.Unsupported} {r.Notes}"); continue; }
            string src = files[fi], edit = P($"v23r-{fi}-edit.pdf");
            page.Contents.Elements.Clear();
            page.Contents.AppendContent().CreateStream(r.Data);
            doc.Save(edit);
            var a = Render(edit, 2, pg); var o = Render(src, 2, pg);
            string ctrl = P($"v23r-{fi}-ctrl.pdf");
            using (var d2 = PdfReader.Open(files[fi], PdfDocumentOpenMode.Modify)) d2.Save(ctrl);
            var cr = Render(ctrl, 2, pg);
            int ctrlDiff = 0;
            for (int i = 0; i + 3 < cr.Length && i + 3 < o.Length; i += 4)
                if (Math.Abs(cr[i] - o[i]) > 64 || Math.Abs(cr[i + 1] - o[i + 1]) > 64 || Math.Abs(cr[i + 2] - o[i + 2]) > 64) ctrlDiff++;
            var cb = page.CropBox.IsZero ? page.MediaBox : page.CropBox;
            int widthPx = (int)Math.Ceiling((cb.X2 - cb.X1) * 2);
            var clusters = new Dictionary<int, (double, double, int)>(); int outside = 0, insideDiff = 0; double ox0 = 1e9, oy0 = 1e9, ox1 = -1e9, oy1 = -1e9;
            for (int i = 0; i + 3 < a.Length && i + 3 < o.Length; i += 4)
            {
                bool d = Math.Abs(a[i] - o[i]) > 64 || Math.Abs(a[i + 1] - o[i + 1]) > 64 || Math.Abs(a[i + 2] - o[i + 2]) > 64;
                if (!d) continue;
                int px = (i / 4) % widthPx, py = (i / 4) / widthPx;
                double X = cb.X1 + px / 2.0, Y = cb.Y2 - py / 2.0;
                bool inRegion = r.Regions!.Any(rg =>
                {
                    double th = Math.Atan2(rg.Y1 - rg.Y0, rg.X1 - rg.X0), nx = -Math.Sin(th) * 1.3 * rg.Size, ny = Math.Cos(th) * 1.3 * rg.Size;
                    var xs = new[] { rg.X0 + nx, rg.X0 - nx, rg.X1 + nx, rg.X1 - nx }; var ys = new[] { rg.Y0 + ny, rg.Y0 - ny, rg.Y1 + ny, rg.Y1 - ny };
                    return X >= xs.Min() - 3 && X <= xs.Max() + 3 && Y >= ys.Min() - 3 && Y <= ys.Max() + 3;
                });
                if (inRegion) insideDiff++; else { outside++; var key = (int)(Y / 6); clusters[key] = clusters.TryGetValue(key, out var cc) ? (Math.Min(cc.Item1, X), Math.Max(cc.Item2, X), cc.Item3 + 1) : (X, X, 1); ox0 = Math.Min(ox0, X); oy0 = Math.Min(oy0, Y); ox1 = Math.Max(ox1, X); oy1 = Math.Max(oy1, Y); }
            }
            int oldLeft = FindProbe.Find(FindProbe.BuildLines(FindProbe.Walk(PdfReader.Open(edit, PdfDocumentOpenMode.Import).Pages[pg]).Glyphs), token, true, false).Count;
            int fonts = ms.SelectMany(m => m.Glyphs).Select(g => g.FontRes).Distinct().Count(); int mixed = ms.Count(m => m.Glyphs.Select(g => (g.FontRes, Math.Round(g.FontSize, 2))).Distinct().Count() > 1); int multiOp = ms.Count(m => m.Glyphs.Select(g => g.Op).Distinct().Count() > 1); var digitsAvail = string.Join("/", ms.Select(m => w.Fonts[m.Glyphs[0].FontRes]).Distinct().Select(f => f.Book is null ? (f.Embedded ? "no-codebook" : "nonembedded-direct") : f.Book.Available.Count(char.IsDigit).ToString()));
            sb.AppendLine($"real#{fi + 1}: page={pg + 1} tokenLen={token.Length} matches={ms.Count} (expected>={count}) replaced={r.Replaced} needsAppFont={r.NeedsAppFont} unsupported={r.Unsupported} overlapWarn={r.Overlaps} fontsInvolved={fonts} matchesWithMixedFontOrSize={mixed} matchesSpanningSeveralOps={multiOp} digitsInFontCodebook(of 10)=[{digitsAvail}] | OUTSIDE-regions diff={outside}px INSIDE(changed)={insideDiff}px | oldTokenLeftAfter(mine)={oldLeft} controlResaveOnlyDiff={ctrlDiff}px clusters=[{string.Join(" ", clusters.Select(kv => $"y~{kv.Key * 6}:x{kv.Value.Item1:0}-{kv.Value.Item2:0}n{kv.Value.Item3}"))}] ops=[{string.Join(" ", ms.Select(m => $"{pc0(w, m)}"))}] outsideBox=({ox0:0},{oy0:0})-({ox1:0},{oy1:0}) regions=[{string.Join(" ", r.Regions!.Select(q => $"({q.X0:0},{q.Y0:0},sz{q.Size:0.0})"))}] | replaceTime={replMs:0.0} ms");
        }
        File.WriteAllText(Out("v040-v23-real.txt"), sb.ToString());
    }

    [Fact]
    public void V22_Performance_ManyLinesPage_And_LargeDrawing()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== V22 성능: 쪽 해석(글 상태 따라가며 글자 위치)+논리 글줄+찾기. 쪽 열기(PdfSharp)와 내용 읽기·분석을 나눠 잼(콜드 1회, 이어서 웜 3회 중 최소)");
        string many = P("v22-many5000.pdf");
        PdfEditor.TestFixtures.ContentPdfFactory.CreateManyTextLines(many, 5000);
        sb.AppendLine(PerfOne("synthetic 5,000 text lines (A4)", many, 0));
        string? dir = PrivateDir();
        if (dir is not null)
        {
            string a1 = Path.Combine(dir, "테스트용", "1_A1도면_큰파일.pdf");
            if (File.Exists(a1)) sb.AppendLine(PerfOne("large drawing page 1", a1, 0));
            string big = Path.Combine(dir, "테스트용", "2_300쪽.pdf");
            if (File.Exists(big))
            {
                using var doc = PdfReader.Open(big, PdfDocumentOpenMode.Import);
                var swAll = Stopwatch.StartNew();
                int glyphs = 0, lines = 0, found = 0;
                for (int p = 0; p < doc.PageCount; p++)
                {
                    var w = FindProbe.Walk(doc.Pages[p]); var l = FindProbe.BuildLines(w.Glyphs);
                    glyphs += w.Glyphs.Count; lines += l.Count; found += FindProbe.Find(l, "the", false, true).Count;
                }
                sb.AppendLine($"300-page document, all pages sequential: pages={doc.PageCount} glyphs={glyphs} lines={lines} total={swAll.Elapsed.TotalSeconds:0.00} s ({swAll.Elapsed.TotalMilliseconds / doc.PageCount:0.0} ms/page)");
            }
        }
        File.WriteAllText(Out("v040-v22-perf.txt"), sb.ToString());
    }

    private static string PerfOne(string label, string path, int pageIndex)
    {
        var swOpen = Stopwatch.StartNew();
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var page = doc.Pages[pageIndex];
        double openMs = swOpen.Elapsed.TotalMilliseconds;
        double best = double.MaxValue, cold = 0; int glyphs = 0, lines = 0, matches = 0, ops = 0;
        for (int k = 0; k < 4; k++)
        {
            var sw = Stopwatch.StartNew();
            var w = FindProbe.Walk(page);
            var l = FindProbe.BuildLines(w.Glyphs);
            matches = FindProbe.Find(l, "1", false, true).Count;
            double ms = sw.Elapsed.TotalMilliseconds;
            if (k == 0) cold = ms; else best = Math.Min(best, ms);
            glyphs = w.Glyphs.Count; lines = l.Count; ops = w.Pc?.Ops.Count ?? 0;
        }
        return $"{label}: open={openMs:0} ms, contentOps={ops}, glyphs={glyphs}, lines={lines}, cold={cold:0} ms, warm(min of 3)={best:0} ms (find incl.)";
    }

    /// <summary>쪽의 Do 연산이 부르는 폼 XObject 안의 글 보이기 연산 수(폼 안 다시 부르는 폼까지, 깊이 3, 같은 폼은 한 번만)와 폼 수.</summary>
    private static (int Forms, int TextOps) FormTextOps(PdfSharp.Pdf.PdfPage page, ParsedContent pc)
    {
        var seen = new HashSet<string>(); int forms = 0, ops = 0;
        void Visit(PdfSharp.Pdf.PdfDictionary? xobjs, IEnumerable<ContentOp> list, int depth)
        {
            if (xobjs is null || depth > 3) return;
            foreach (var op in list.Where(o => o.Name == "Do"))
            {
                string? name = op.Operands.LastOrDefault(o => o.Kind == OperandKind.Name)?.Text;
                if (name is null) continue;
                var x = xobjs.Elements.GetDictionary("/" + name);
                if (x is null || x.Elements.GetName("/Subtype") != "/Form" || x.Stream is null) continue;
                string key = depth + ":" + name;
                if (!seen.Add(key)) continue;
                forms++;
                var inner = ContentLexer.Parse(x.Stream.UnfilteredValue);
                ops += inner.Ops.Count(o => o.Name is "Tj" or "TJ" or "'" or "\"");
                var innerX = x.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject") ?? xobjs;
                Visit(innerX, inner.Ops, depth + 1);
            }
        }
        Visit(page.Resources?.Elements.GetDictionary("/XObject"), pc.Ops, 0);
        return (forms, ops);
    }

    [Fact]
    public void V23_RealPdfs_DocumentLevelCodebook_DigitCoverage()
    {
        string? dir = PrivateDir();
        if (dir is null) return;
        var sb = new StringBuilder();
        sb.AppendLine("== V23 실제 PDF: 같은 글꼴 사전을 쓰는 모든 쪽의 보인 글 코드를 모아 코드북을 만들면 숫자 0~9가 몇 개 들어 있나 (쪽 단위 대비)");
        var tok = new Regex(@"\b[A-Z]{1,3}-?\d{3,5}\b", RegexOptions.Compiled);
        var files = Directory.GetFiles(dir, "*.pdf").OrderBy(f => f, StringComparer.Ordinal).ToList();
        for (int fi = 0; fi < files.Count; fi++)
        {
            using var doc = PdfReader.Open(files[fi], PdfDocumentOpenMode.Import);
            int pages = Math.Min(doc.PageCount, 60);
            var strs = new Dictionary<int, List<ReadOnlyMemory<byte>>>();   // 글꼴 사전 객체 번호 → 보인 문자열
            var dicts = new Dictionary<int, PdfSharp.Pdf.PdfDictionary>();
            var perPageDigits = new List<(int Id, int Digits)>();
            for (int p = 0; p < pages; p++)
            {
                var page = doc.Pages[p];
                var loaded = PageContent.Load(page);
                if (loaded.Data is null) continue;
                var pc = ContentLexer.Parse(loaded.Data);
                var fonts = page.Resources?.Elements.GetDictionary("/Font");
                string? cur = null;
                foreach (var op in pc.Ops)
                {
                    if (op.Name == "Tf") cur = op.Operands.FirstOrDefault(o => o.Kind == OperandKind.Name)?.Text;
                    else if (cur is not null && op.Name is "Tj" or "TJ" or "'" or "\"")
                    {
                        var fd = fonts?.Elements.GetDictionary("/" + cur);
                        int id = fd?.Reference?.ObjectNumber ?? -1;
                        if (fd is null || id < 0) continue;
                        dicts[id] = fd;
                        if (!strs.TryGetValue(id, out var l)) strs[id] = l = [];
                        foreach (var o in op.Operands) Collect(o, l);
                    }
                }
            }
            int fontsTotal = 0, withBook = 0; var digitCounts = new List<int>();
            foreach (var (id, l) in strs)
            {
                fontsTotal++;
                var book = PdfEditor.ContentStream.Fonts.FontCodebook.TryBuild(dicts[id], l);
                if (book is null) continue;
                withBook++;
                int digits = book.Available.Count(char.IsDigit);
                if (digits > 0) digitCounts.Add(digits);   // 숫자를 쓰는 글꼴만
            }
            sb.AppendLine($"real#{fi + 1}: pages scanned={pages}, font dictionaries used={fontsTotal}, with codebook={withBook}, fonts that show digits: {digitCounts.Count} -> digits available (of 10) per font: [{string.Join(",", digitCounts.OrderBy(x => x))}]");
        }
        File.WriteAllText(Out("v040-v23-doclevel.txt"), sb.ToString());
    }

    private static void Collect(Operand o, List<ReadOnlyMemory<byte>> into)
    {
        if (o.Kind == OperandKind.String) into.Add(o.Bytes);
        else if (o.Kind == OperandKind.Array) foreach (var x in o.Items) Collect(x, into);
    }

    [Fact]
    public void V23_Control_PerGlyphSource_VsWholeSource_BaselineNoise()
    {
        string a = P("ctl-perglyph.pdf"), b = P("ctl-whole.pdf");
        DrawingIdPdfFactory.Create(a, IdCase.PerGlyph);
        DrawingIdPdfFactory.Create(b, IdCase.Whole);
        var (dpx, ink, pct) = Compare(Render(a), Render(b));
        string box = LastBox;
        File.WriteAllText(Out("v040-v23-control.txt"), $"편집 없는 PerGlyph 원본 vs Whole 원본(같은 번호): 쪽 전체 차이 {dpx}px / 잉크 {ink}px ({pct:0.00}%) {box}\n");
    }

    private string PdfiumText(string path, int page) =>
        _t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(_t, path, null);
            using var p = d.LoadPage(page);
            using var tp = p.LoadText();
            return tp.CharCount == 0 ? "" : tp.GetText(0, tp.CharCount);
        }).GetAwaiter().GetResult();

    private static string pc0(WalkResult w, PMatch m) { var g = m.Glyphs[0]; var op = w.Pc!.Ops[g.Op]; int total = w.Glyphs.Count(x => x.Op == g.Op); return $"{op.Name}(op glyphs {total}, matched {m.Glyphs.Count}, Tz {g.Tz:0}, Tc {g.Tc:0.##}, font {w.Fonts[g.FontRes].Type0})"; }

    private static int CountOcc(string text, string q) { int n = 0; for (int i = text.IndexOf(q, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(q, i + q.Length, StringComparison.OrdinalIgnoreCase)) n++; return n; }

    private int PdfiumPageCount(string path, int page, string q) => PdfiumCount(path, page, q, false, false);
}
