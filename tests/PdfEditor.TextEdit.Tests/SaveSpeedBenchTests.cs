using System.Diagnostics;
using System.Text;
using Pdf.Native;
using PdfEditor.Appearance;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// 0.14.0 저장 속도 측정 하네스. 환경 변수 SEDOL_SAVE_BENCH=1일 때만 돈다(평소 시험에서는 바로 끝남).
/// 시나리오 = 파일(작은 합성 1쪽 / 합성 40쪽 / SEDOL_BENCH_PDF가 가리키는 큰 실제 도면) × 편집(없음 / 사각형 주석 / 선 주석 / 글 한 줄 수정).
/// 앱과 같은 SaveService(AP 공급자 + PDFsharp 패처)로 저장하고 <see cref="SaveTrace"/>로 단계별 ms를 모아 중앙값을 낸다.
/// 결과: %TEMP%\sedol-save-bench.md (표). 실제 고객 PDF는 읽기만 하고 저장소에 넣지 않는다.
/// </summary>
public sealed class SaveSpeedBenchTests : IDisposable
{
    private static readonly Lazy<AppFont> Font = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppFont.Load(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
    });

    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sedol-save-bench-" + Guid.NewGuid().ToString("N"));
    private readonly AnnotationAssets _assets = new();

    public SaveSpeedBenchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SaveTrace.Sink = null;
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private enum Edit { None, Square, Line, TextLine }

    [Fact]
    public async Task MeasureSaveStages()
    {
        if (Environment.GetEnvironmentVariable("SEDOL_SAVE_BENCH") != "1") return;
        int repeats = int.TryParse(Environment.GetEnvironmentVariable("SEDOL_BENCH_REPEATS"), out var rp) ? rp : 3;

        var files = new List<(string Name, string Path)>();
        string small = Path.Combine(_dir, "small-titleblock.pdf");
        ContentPdfFactory.CreateThreeColumnTjLine(small);
        files.Add(("small (synthetic 1p)", small));
        string doc40 = Path.Combine(_dir, "doc40.pdf");
        ContentPdfFactory.CreateDocumentLike(doc40, pages: 40);
        files.Add(("synthetic 40p", doc40));
        string? big = Environment.GetEnvironmentVariable("SEDOL_BENCH_PDF");
        if (big is not null && File.Exists(big))
        {
            // 고객 원본은 건드리지 않는다: 임시 폴더로 복사해서 쓴다.
            string copy = Path.Combine(_dir, "big-drawing.pdf");
            File.Copy(big, copy);
            files.Add(($"real drawing ({new FileInfo(big).Length / 1024 / 1024.0:F1} MB)", copy));
        }

        var stages = new Dictionary<string, Dictionary<string, List<double>>>();   // 시나리오 → 단계 → 반복별 합
        var notes = new List<string>();
        var sink = new Dictionary<string, double>();
        SaveTrace.Sink = (name, ms) => { lock (sink) sink[name] = sink.GetValueOrDefault(name) + ms; };

        // 데우기(JIT·글꼴 로딩): 작은 파일 글 수정 한 번 — 결과는 버린다.
        await RunOnce(small, Edit.TextLine, Path.Combine(_dir, "warm.pdf"), null);
        await RunOnce(small, Edit.Square, Path.Combine(_dir, "warm2.pdf"), null);
        lock (sink) sink.Clear();

        foreach (var (fname, fpath) in files)
        {
            int pages = await _t.InvokeAsync(() => { using var d = PdfDocument.Open(_t, fpath, null); return d.PageCount; });
            foreach (var edit in Enum.GetValues<Edit>())
            {
                string key = $"{fname}, {pages}p | {edit}";
                var per = stages[key] = [];
                for (int r = 0; r < repeats; r++)
                {
                    lock (sink) sink.Clear();
                    string target = Path.Combine(_dir, $"out-{Guid.NewGuid():N}.pdf");
                    File.Copy(fpath, target);   // 같은 경로 덮어쓰기(앱의 Ctrl+S와 같은 File.Replace 경로)
                    string? note = await RunOnce(target, edit, target, notes);
                    if (note == "skip") { stages.Remove(key); break; }
                    lock (sink) foreach (var (k, v) in sink) (per.TryGetValue(k, out var l) ? l : per[k] = []).Add(v);
                    try { File.Delete(target); } catch { }
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Save stage timings (median of {repeats}, ms) — {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        foreach (var (key, per) in stages)
        {
            sb.AppendLine($"## {key}");
            sb.AppendLine("| stage | ms (median) | min | max |");
            sb.AppendLine("|---|---:|---:|---:|");
            foreach (var (stage, list) in per.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var sorted = list.OrderBy(x => x).ToList();
                sb.AppendLine($"| {stage} | {sorted[sorted.Count / 2]:F0} | {sorted[0]:F0} | {sorted[^1]:F0} |");
            }
            sb.AppendLine();
        }
        if (notes.Count > 0) { sb.AppendLine("Notes:"); foreach (var n in notes.Distinct()) sb.AppendLine("- " + n); }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sedol-save-bench.md"), sb.ToString());
    }

    /// <summary>열기 → 편집 하나 → 저장(target). 열기·편집 시간은 재지 않는다(Sink는 SaveService 안 단계만 받는다).</summary>
    private async Task<string?> RunOnce(string src, Edit edit, string target, List<string>? notes)
    {
        var sink = SaveTrace.Sink;
        SaveTrace.Sink = null;
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        await s.AnnotationsReady;
        var page = s.Pages[0].Id;
        switch (edit)
        {
            case Edit.Square:
                await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(page, new Annotation
                {
                    Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(50, 50, 150, 120),
                    BorderWidth = 1f, Color = new PdfColor(229, 55, 45), Flags = PdfAnnotFlags.Print,
                }));
                break;
            case Edit.Line:
                await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(page, new Annotation
                {
                    Id = s.Annotations.NewId(), Kind = AnnotKind.Line, Rect = new PdfRectF(50, 50, 250, 150),
                    BorderWidth = 2f, Color = new PdfColor(229, 55, 45), Flags = PdfAnnotFlags.Print,
                    Vertices = [new PdfPointF(50, 50), new PdfPointF(250, 150)],
                }));
                break;
            case Edit.TextLine:
                {
                    var plan = await _t.InvokeAsync<(ReplacePlan? p, string? desc)>(() =>
                    {
                        var entries = s.GetObjectIndex(page).Entries.Where(e => e.Type == PdfPageObjectType.Text && (e.TextPreview ?? "").Trim().Length >= 3).ToList();
                        foreach (var e in entries.Take(200))
                        {
                            string old = e.TextPreview!.TrimEnd();
                            char last = old[^1];
                            string nt = old[..^1] + (char.IsDigit(last) ? (char)('0' + (last - '0' + 1) % 10) : last == 'A' ? 'B' : 'A');
                            var p = TextEditPlanner.PlanReplace(s, page, e.Ref, nt, Font.Value);
                            if (p.Refusal == ReplaceRefusal.None) return ((ReplacePlan?)p, $"'{old}' -> '{nt}'");
                        }
                        return (null, null);
                    });
                    if (plan.p is null) { notes?.Add($"{Path.GetFileName(src)}: no editable text line found on page 1 — TextLine skipped"); SaveTrace.Sink = sink; return "skip"; }
                    notes?.Add($"{Path.GetFileName(src)}: TextLine edit {plan.desc}");
                    await s.Commands.ExecuteAsync(new ContentEditCommand(page, plan.p.Edits));
                    break;
                }
        }
        SaveTrace.Sink = sink;
        var svc = new SaveService([], new PdfAppearanceSource(Font.Value, _assets), new PdfSharpAnnotationPatcher(Font.Value), s.Assets);
        await svc.SaveAsync(s, target);
        if (svc.LastRepairedPages.Count > 0) notes?.Add($"{Path.GetFileName(src)}: repaired pages {string.Join(",", svc.LastRepairedPages)}");
        return null;
    }
}

/// <summary>저장 맨 끝 "쪽 내용 고치기 검사"(RepairContent)가 무엇에 시간을 쓰는지 쪼개 본다. SEDOL_SAVE_BENCH=1 + SEDOL_BENCH_PDF일 때만.</summary>
public sealed class RepairScanBreakdownTests
{
    [Fact]
    public void BreakDownRepairScan()
    {
        if (Environment.GetEnvironmentVariable("SEDOL_SAVE_BENCH") != "1") return;
        string? big = Environment.GetEnvironmentVariable("SEDOL_BENCH_PDF");
        if (big is null || !File.Exists(big)) return;
        var sb = new StringBuilder();
        for (int round = 0; round < 2; round++)
        {
            double tOpen, tLoad = 0, tLex = 0, tRepair = 0, tDangling = 0, tUnmatched = 0, tJoinLexOnly = 0;
            long bytes = 0, ops = 0; int streams = 0, pages, multi = 0, maxPageMs = 0, maxPage = -1;
            var sw = Stopwatch.StartNew();
            using var doc = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(File.ReadAllBytes(big)), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
            tOpen = sw.Elapsed.TotalMilliseconds;
            pages = doc.PageCount;
            for (int i = 0; i < pages; i++)
            {
                var p0 = Stopwatch.StartNew();
                sw.Restart();
                var loaded = PdfEditor.ContentStream.PageContent.LoadStreams(doc.Pages[i]);
                tLoad += sw.Elapsed.TotalMilliseconds;
                if (loaded.Streams is null) continue;
                streams += loaded.Streams.Count; if (loaded.Streams.Count > 1) multi++;
                sw.Restart();
                foreach (var s in loaded.Streams) { bytes += s.Length; ops += PdfEditor.ContentStream.ContentLexer.Parse(s).Ops.Count; }
                tLex += sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                PdfEditor.ContentStream.ContentRepair.RepairStreams(loaded.Streams);
                tRepair += sw.Elapsed.TotalMilliseconds;
                var joined = loaded.Streams.SelectMany(x => x.Append((byte)'\n')).ToArray();
                sw.Restart();
                PdfEditor.ContentStream.ContentLexer.Parse(joined);
                tJoinLexOnly += sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                PdfEditor.ContentStream.ContentRepair.RemoveDanglingFragments(joined);
                tDangling += sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                PdfEditor.ContentStream.ContentRepair.RemoveUnmatchedRestores(joined);
                tUnmatched += sw.Elapsed.TotalMilliseconds;
                int pm = (int)p0.ElapsedMilliseconds; if (pm > maxPageMs) { maxPageMs = pm; maxPage = i + 1; }
            }
            sb.AppendLine($"round {round}: pages {pages}, streams {streams} (multi-stream pages {multi}), decoded content {bytes / 1024.0 / 1024:F1} MB, ops {ops:N0}");
            sb.AppendLine($"  pdfsharp open {tOpen:F0} ms | LoadStreams(decode) {tLoad:F0} | lex each stream once {tLex:F0} | RepairStreams total {tRepair:F0}");
            sb.AppendLine($"  (separately) lex joined once {tJoinLexOnly:F0} | RemoveDanglingFragments {tDangling:F0} | RemoveUnmatchedRestores {tUnmatched:F0} | slowest page {maxPage} ({maxPageMs} ms, all measurements)");
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sedol-repair-breakdown.txt"), sb.ToString());
    }
}
