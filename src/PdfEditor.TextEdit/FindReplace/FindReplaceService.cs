using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.ContentStream;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Commands;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.FindReplace;

/// <summary>찾기 결과 한 벌과 그것이 쓰는 PDF 문서(바꾸기 계획이 같은 쪽 내용을 다시 쓴다). 다 쓰면 Dispose.</summary>
public sealed class FindOutcome(FindReport report, PdfSharp.Pdf.PdfDocument doc) : IDisposable
{
    public FindReport Report { get; } = report;
    internal PdfSharp.Pdf.PdfDocument Document { get; } = doc;
    internal DocumentFontBooks Books { get; } = new(doc);
    public IEnumerable<TextMatch> Matches => Report.Pages.SelectMany(p => p.Matches);
    internal string? TempPath { get; init; }
    public void Dispose() { Document.Dispose(); if (TempPath is not null) FindReplaceService.TryDelete(TempPath); }
}

/// <summary>바꿀 후보 하나(미리보기 한 줄). Ok가 아니면 Refusal이 이유다("못 바꾼 N"로 센다).</summary>
public sealed class ReplaceItem
{
    public required TextMatch Match { get; init; }
    public required WordReplacement Plan { get; init; }
    public WordRefusal Refusal { get; init; }
    public ReplaceFontKind Font { get; init; }
    public bool Overlap { get; init; }
    public bool Ok => Refusal == WordRefusal.None;
    /// <summary>앱 글꼴이면 만든 줄(화면·파일 공용).</summary>
    internal AppTextSpec? App { get; init; }
    /// <summary>앱 글꼴 줄이 놓일 글꼴 선택(굵게·기울임·명조 흉내 포함)은 App에 들어 있다. 이 번호는 BuildCommand가 편집 Id를 정할 때 쓴다.</summary>
    internal long ScreenId { get; set; }
    internal PageId Page => Match.Page;
    internal int FirstOp => Plan.FirstOp;
}

/// <summary>"모두 바꾸기" 계획: 미리보기 목록과 요약.</summary>
public sealed class ReplaceBatch
{
    public required IReadOnlyList<ReplaceItem> Items { get; init; }
    internal Dictionary<(PageId Page, int Op), ObjectContext> Objects { get; init; } = [];
    public int OkCount => Items.Count(i => i.Ok);
    public int AppFontCount => Items.Count(i => i.Ok && i.Font == ReplaceFontKind.App && i.Plan.NewText.Length > 0);
    public int RefusedCount => Items.Count(i => !i.Ok);
    public int OverlapCount => Items.Count(i => i.Ok && i.Overlap);
    /// <summary>못 바꾼 항목이 있는 쪽(1부터).</summary>
    public IReadOnlyList<int> RefusedPages => [.. Items.Where(i => !i.Ok).Select(i => i.Match.PageIndex + 1).Distinct().Order()];
}

internal sealed record ObjectContext(int ObjectIndex, PdfMatrix SrcMatrix, PageTextIndex Source);

/// <summary>
/// 찾아 바꾸기 서비스(0.4.0, 화면 없음): 찾기(<see cref="FindAsync"/>) → 계획(<see cref="PlanAsync"/>, 미리보기 목록) → 명령(<see cref="BuildCommand"/>, 실행 취소 한 번).
/// 파일은 편집 중인 문서가 아니라 열 때의 파일(<see cref="DocumentSession.FontSourcePath"/> 또는 <see cref="DocumentSession.FilePath"/>)을 읽는다 —
/// 이미 바꾼·지운 글은 후보에서 뺀다. 같은 세션에서 바꾼 새 번호는 저장하기 전에는 찾을 수 없다(알려진 한계).
/// </summary>
public static class FindReplaceService
{
    private static string? SourceOf(DocumentSession s) =>
        s.FontSourcePath is { } fsp && File.Exists(fsp) ? fsp : s.FilePath is { } fp && File.Exists(fp) ? fp : null;

    /// <summary>찾는다(백그라운드, 쪽 단위 진행률·취소). 파일이 없는 새 문서는 InvalidOperationException, 쪽 수가 파일과 다르면(쪽을 지우거나 넣은 뒤) InvalidOperationException("page-structure-changed").</summary>
    public static Task<FindOutcome> FindAsync(DocumentSession s, FindOptions opts, IReadOnlyList<PageId>? onlyPages = null, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        return FindCoreAsync(s, opts, onlyPages, progress, ct);
    }

    private static async Task<FindOutcome> FindCoreAsync(DocumentSession s, FindOptions opts, IReadOnlyList<PageId>? onlyPages, IProgress<int>? progress, CancellationToken ct)
    {
        var ids = s.Pages.Select(p => p.Id).ToList();
        var edits = ids.Select(id => s.Content.For(id)).ToList();
        var indexes = onlyPages?.Select(p => ids.IndexOf(p)).Where(i => i >= 0).ToList();
        string? pw = s.Password;
        // 지금 화면의 문서와 어긋나지 않는 파일: 암호가 없으면 편집 중인 PDFium 문서를 그대로 임시 사본으로 떠서 읽는다(저장 길 OutputCopyBuilder의 첫 단계와 같은 SaveCopy —
        // 원본 내용·쪽 구성이 지금 문서와 같고 객체 번호가 PDFium 객체 번호와 맞는다). 원본 파일이 없거나 옮겨졌거나 쪽을 넣고 지운 뒤에도 찾아진다(0.4.0 실제 도면 결함).
        // 암호 문서는 사본을 풀어 둘 수 없어 열 때의 파일을 읽는다.
        string source;
        string? temp = null;
        if (!s.IsEncrypted)
        {
            temp = Path.Combine(Path.GetTempPath(), "SedolPDF-find-" + Guid.NewGuid().ToString("N") + ".pdf");
            string t = temp;
            try { await s.Thread.InvokeAsync(() => s.Document.SaveCopy(t), ct).ConfigureAwait(false); }
            catch { TryDelete(t); throw; }
            source = temp;
        }
        else source = SourceOf(s) ?? throw new InvalidOperationException("no-file");
        return await Task.Run(() =>
        {
            PdfSharp.Pdf.PdfDocument? doc = null;
            try
            {
                doc = pw is null ? PdfReader.Open(source, PdfDocumentOpenMode.Import) : PdfReader.Open(source, pw, PdfDocumentOpenMode.Import);
                if (doc.PageCount != ids.Count) throw new InvalidOperationException("page-structure-changed");
                var report = DocumentTextFinder.FindAll(doc, opts, ids, indexes, progress, ct);
                return new FindOutcome(DropEdited(report, edits), doc) { TempPath = temp };
            }
            catch { doc?.Dispose(); if (temp is not null) TryDelete(temp); throw; }
        }, ct).ConfigureAwait(false);
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>이미 바꾼 낱말·지운 글 객체에 걸친 후보를 뺀다(같은 글을 두 번 바꾸지 않게).</summary>
    private static FindReport DropEdited(FindReport report, List<ImmutableArray<ContentEdit>> editsByPage)
    {
        var pages = new List<PageFindResult>();
        foreach (var pr in report.Pages)
        {
            var edits = pr.PageIndex < editsByPage.Count ? editsByPage[pr.PageIndex] : [];
            if (pr.Matches.Count == 0 || edits.IsDefaultOrEmpty || edits.Length == 0) { pages.Add(pr); continue; }
            var dead = new HashSet<int>();
            var cut = new HashSet<(int Obj, int Str, int Code)>();
            foreach (var e in edits)
                switch (e)
                {
                    case DeactivateEdit { Target.IsInserted: false } d: dead.Add(d.Target.OriginalIndex); break;
                    case ReplaceParagraphEdit pe: foreach (var r in pe.Remove) if (!r.IsInserted) dead.Add(r.OriginalIndex); break;
                    case ReplaceTextSpanEdit te when !te.Original.IsInserted:
                        foreach (var sp in te.Spans) foreach (var c in sp.Cuts) for (int k = 0; k < c.Count; k++) cut.Add((te.Original.OriginalIndex, c.Str, c.FromCode + k));
                        break;
                }
            var src = pr.Matches[0].Source!;
            var seg = ContentSegmenter.Segment(src.Walk.Streams[0], PageContent.XObjectResolver(src.Page));
            var objOfOp = seg.Ok ? seg.Objects.Select((o, i) => (o, i)).Where(x => x.o.Kind == ObjectKind.Text).ToDictionary(x => x.o.OpIndex, x => x.i) : [];
            bool Edited(TextMatch m) => m.Glyphs.Any(g => !g.InForm && objOfOp.TryGetValue(g.Op, out int obj) && (dead.Contains(obj) || cut.Contains((obj, g.Str, g.Code))));
            pages.Add(pr with { Matches = [.. pr.Matches.Where(m => !Edited(m))] });
        }
        return report with { Pages = pages };
    }

    /// <summary>
    /// 바꾸기 계획을 세운다(PDFium 전용 스레드에서 객체 번호 대응·원래 글꼴 확인을 한다). 못 바꾸는 후보도 목록에 이유와 함께 남는다.
    /// 앱 글꼴이 필요한 낱말은 그 낱말만 앱 글꼴(N1)이며 <see cref="ReplaceBatch.AppFontCount"/>로 센다.
    /// </summary>
    public static Task<ReplaceBatch> PlanAsync(DocumentSession s, FindOutcome outcome, IReadOnlyList<TextMatch> matches, string newText,
        AppFont? appFont = null, AppFontCatalog? catalog = null, CancellationToken ct = default)
    {
        var plans = matches.Select(m => ReplacePlanner.Plan(m, newText, outcome.Books.Get)).ToList();
        return s.Thread.InvokeAsync(() => PlanOnThread(s, plans, newText, appFont, catalog), ct);
    }

    private static ReplaceBatch PlanOnThread(DocumentSession s, List<WordReplacement> plans, string newText, AppFont? appFont, AppFontCatalog? catalog)
    {
        s.Thread.AssertOnThread();
        var items = new List<ReplaceItem>();
        var objects = new Dictionary<(PageId, int), ObjectContext>();
        bool canPatch = SavePolicy.CanPatch(s);
        foreach (var pageGroup in plans.GroupBy(p => p.Match.Page))
        {
            var pageId = pageGroup.Key;
            int pageIndex = pageId.Value == 0 ? -1 : s.IndexOf(pageId);
            Dictionary<int, int>? objOfOp = null;
            ReplayResult? replay = null;
            string? pageProblem = null;
            if (pageIndex < 0) pageProblem = "gone";
            else
            {
                var src = pageGroup.First().Match.Source!;
                var seg = ContentSegmenter.Segment(src.Walk.Streams[0], PageContent.XObjectResolver(src.Page));
                replay = s.GetReplay(pageId);
                bool same = seg.Ok && seg.Objects.Count == replay.Originals.Count;
                for (int i = 0; same && i < seg.Objects.Count; i++) same = (int)seg.Objects[i].Kind == (int)replay.Originals[i].Type;
                if (!same) pageProblem = "mismatch";
                else objOfOp = seg.Objects.Select((o, i) => (o, i)).Where(x => x.o.Kind == ObjectKind.Text).ToDictionary(x => x.o.OpIndex, x => x.i);
            }
            var verified = new Dictionary<int, bool>();   // 글 객체 번호 → 원래 글꼴로 화면에 그려지는가(V14)
            foreach (var plan in pageGroup)
            {
                ReplaceItem Refused(WordRefusal r) => new() { Match = plan.Match, Plan = plan, Refusal = r, Font = plan.Font };
                if (!plan.Ok) { items.Add(Refused(plan.Refusal)); continue; }
                if (!canPatch) { items.Add(Refused(WordRefusal.Encrypted)); continue; }
                if (pageProblem is not null || objOfOp is null || replay is null) { items.Add(Refused(pageProblem == "gone" ? WordRefusal.ObjectNotFound : WordRefusal.PageMismatch)); continue; }
                if (plan.OpCuts.Any(c => !objOfOp.ContainsKey(c.Op))) { items.Add(Refused(WordRefusal.ObjectNotFound)); continue; }
                int firstObj = objOfOp[plan.FirstOp];
                if (replay.Originals[firstObj] is not { Type: PdfPageObjectType.Text } firstSrc) { items.Add(Refused(WordRefusal.ObjectNotFound)); continue; }
                foreach (var (op, _) in plan.OpCuts)
                    if (!objects.ContainsKey((pageId, op)) && replay.Originals[objOfOp[op]] is { } so)
                        objects[(pageId, op)] = new ObjectContext(objOfOp[op], so.Matrix, plan.Match.Source!);

                var kind = plan.Font;
                if (newText.Length > 0 && kind != ReplaceFontKind.App)
                {
                    // 화면(PDFium 글 객체)도 같은 글꼴로 그려지는지(V14 규칙). 안 되면 이 낱말은 앱 글꼴.
                    if (!verified.TryGetValue(firstObj, out bool ok))
                    {
                        ok = OriginalFontReplacer.Verify(s, pageIndex, firstObj, newText, kind == ReplaceFontKind.WinAnsiDirect || plan.Book is null ? null : plan.Book.Available.ToHashSet()) == OriginalFontReplacer.Result.Ok;
                        verified[firstObj] = ok;
                    }
                    if (!ok) kind = ReplaceFontKind.App;
                }

                AppTextSpec? app = null;
                if (newText.Length > 0 && kind == ReplaceFontKind.App)
                {
                    var first = plan.Match.Glyphs[0];
                    var m = firstSrc.Matrix;
                    var placement = new PdfMatrix(m.A, m.B, m.C, m.D, (float)first.Place.E, (float)first.Place.F);
                    var fc = firstSrc.FillColor;
                    var color = fc is { } c ? new PdfColor(c.R, c.G, c.B) : new PdfColor(0, 0, 0);
                    AppFontChoice? choice = catalog?.Choose(new PdfFontInfo(firstSrc.Font?.BaseName));
                    if (choice is null && (appFont ?? catalog?.Sans) is null) { items.Add(Refused(WordRefusal.NoAppFont)); continue; }
                    var (spec, _) = choice is not null
                        ? AppFontLine.Build(choice, newText, firstSrc.FontSize, placement, color)
                        : AppFontLine.Build((appFont ?? catalog!.Sans)!, newText, firstSrc.FontSize, placement, color);
                    if (spec is null) { items.Add(Refused(WordRefusal.MissingGlyphs)); continue; }
                    app = spec;
                }

                bool overlap = plan.Overlap;
                double len = plan.NewLengthPt;
                if (app is not null)
                {
                    bool vertical = Math.Abs(plan.Match.Glyphs[0].Place.A) < Math.Abs(plan.Match.Glyphs[0].Place.B);
                    len = vertical ? app.Bounds.Height : app.Bounds.Width;
                    overlap = ReplacePlanner.OverlapsFollowing(plan.Match, len);
                }
                items.Add(new ReplaceItem { Match = plan.Match, Plan = plan with { NewLengthPt = len }, Refusal = WordRefusal.None, Font = kind, Overlap = overlap, App = app });
            }
        }
        return new ReplaceBatch { Items = items, Objects = objects };
    }

    /// <summary>
    /// 고른 항목을 실행 취소 한 번짜리 명령으로 묶는다(include가 null이면 바꿀 수 있는 항목 전부). 바꿀 것이 없으면 null.
    /// 같은 글 객체의 낱말은 한 편집으로 합쳐 화면 조각(앞부분·새 글·뒷부분)을 만든다.
    /// </summary>
    public static IUndoableCommand? BuildCommand(DocumentSession s, ReplaceBatch batch, Func<ReplaceItem, bool>? include = null)
    {
        var chosen = batch.Items.Where(i => i.Ok && (include?.Invoke(i) ?? true)).ToList();
        var list = new List<(PageId, ContentEdit)>();
        // 글 객체(쪽, 글 연산)마다 모은다
        var byObject = new Dictionary<(PageId Page, int Op), List<(ReplaceItem Item, ImmutableArray<TextCutRef> Cuts)>>();
        foreach (var it in chosen)
            foreach (var (op, cuts) in it.Plan.OpCuts)
            {
                var key = (it.Page, op);
                if (!byObject.TryGetValue(key, out var l)) byObject[key] = l = [];
                l.Add((it, cuts));
            }
        foreach (var ((page, op), members) in byObject.OrderBy(kv => kv.Key.Page.Value).ThenBy(kv => kv.Key.Op))
        {
            if (!batch.Objects.TryGetValue((page, op), out var ctx)) continue;
            var glyphs = ctx.Source.Walk.Glyphs.Where(g => g.StreamId == 0 && g.Op == op).ToList();
            if (glyphs.Count == 0) continue;
            var spans = ImmutableArray.CreateBuilder<TextSpanReplacement>();
            var screen = ImmutableArray.CreateBuilder<ScreenTextPiece>();
            var appLines = ImmutableArray.CreateBuilder<AppTextSpec>();
            var appIds = ImmutableArray.CreateBuilder<long>();
            var removed = new HashSet<(int Str, int Code)>(members.SelectMany(m => m.Cuts.SelectMany(c => Enumerable.Range(c.FromCode, c.Count).Select(k => (c.Str, k)))));
            // 같은 글 객체를 앞서(다른 번에) 고쳤으면 그 잘린 글자·새 글 조각을 합친다 — 화면 조각이 옛 글 위에 겹쳐 비치지 않게(실제 도면 결함: VIS→UTL 뒤 SENSOR→DETECTOR).
            var priors = s.Content.For(page).OfType<ReplaceTextSpanEdit>().Where(e => !e.Original.IsInserted && e.Original.OriginalIndex == ctx.ObjectIndex).ToList();
            foreach (var pe in priors)
                foreach (var sp in pe.Spans)
                    foreach (var c in sp.Cuts)
                        for (int k = 0; k < c.Count; k++) removed.Add((c.Str, c.FromCode + k));
            // 새 글을 놓을 자리: 낱말의 첫 글자 → 새 글
            var newAt = new Dictionary<(int Str, int Code), ReplaceItem>();
            // 같은 글 연산 안의 뒤따르는 글자 밀기: 낱말을 바꾸면 그 폭 차이만큼 뒤 글자도 같이 민다(0.4.0 실제 도면: "UTL-200"의 하이픈이 L에 붙던 것).
            // 낱말마다 (잘린 글자 끝 위치, 글 공간 이동량 차이). 앞서 고친 편집(priors)이 민 것도 들어 있다.
            var shifts = new List<((int Str, int Code) Last, double Text)>();
            foreach (var pe in priors)
                foreach (var sp in pe.Spans.Where(x => x.AdvanceDeltaText != 0 && !x.Cuts.IsDefaultOrEmpty))
                    shifts.Add(((sp.Cuts[^1].Str, sp.Cuts[^1].FromCode + sp.Cuts[^1].Count - 1), sp.AdvanceDeltaText));
            static bool Before((int Str, int Code) a, (int Str, int Code) b) => a.Str < b.Str || a.Str == b.Str && a.Code < b.Code;
            double ShiftTextBefore((int Str, int Code) pos) => shifts.Where(x => Before(x.Last, pos)).Sum(x => x.Text);
            var ordered = members.OrderBy(m => m.Cuts[0].Str).ThenBy(m => m.Cuts[0].FromCode).ToList();
            for (int mi = 0; mi < ordered.Count; mi++)
            {
                var (it, cuts) = ordered[mi];
                bool carries = it.FirstOp == op;
                int appIndex = -1;
                long screenId = 0;
                byte[]? bytes = null;
                double delta = 0;
                var tm = it.Plan.Tm;
                var cutsOut = cuts;
                if (carries && it.Plan.NewText.Length > 0)
                {
                    var first = (cuts[0].Str, cuts[0].FromCode);
                    var lastCut = cuts[^1];
                    var last = (lastCut.Str, lastCut.FromCode + lastCut.Count - 1);
                    var g0 = it.Plan.Match.Glyphs[0];
                    double scale = Math.Sqrt(g0.Place.A * g0.Place.A + g0.Place.B * g0.Place.B), tz = g0.Tz == 0 ? 1 : g0.Tz / 100.0;
                    double before = ShiftTextBefore(first);
                    if (it.Font == ReplaceFontKind.App && it.App is not null)
                    {
                        appIndex = appLines.Count;
                        appLines.Add(it.App);
                        appIds.Add(s.Content.NewId());
                    }
                    else if (it.Plan.NewBytes is not null)
                    {
                        bytes = it.Plan.NewBytes;
                        screenId = s.Content.NewId();
                        newAt[first] = it;
                        it.ScreenId = screenId;
                        if (before != 0) tm = tm with { E = tm.E + before * tz * tm.A, F = tm.F + before * tz * tm.B };   // 앞 낱말이 민 만큼 새 글도 민다
                    }
                    // 이 낱말이 뒤 글자를 미는 것은: 낱말이 한 글 연산 안에 있고, 앞서 고친 편집이 뒤에 없고, 뒤에 앞 글자 밀림에 기댈 수 없는 앱 글꼴 낱말이 없을 때만.
                    bool laterApp = ordered.Skip(mi + 1).Any(m => m.Item.Font == ReplaceFontKind.App && m.Item.Plan.NewText.Length > 0)
                        || priors.Any(pe => pe.Spans.Any(sp => sp.AppLineIndex >= 0 && !sp.Cuts.IsDefaultOrEmpty && Before(last, (sp.Cuts[0].Str, sp.Cuts[0].FromCode))));
                    bool priorAfter = priors.Any(pe => pe.Spans.Any(sp => !sp.Cuts.IsDefaultOrEmpty && Before(last, (sp.Cuts[0].Str, sp.Cuts[0].FromCode))));
                    bool appNeedsShift = appIndex >= 0 && before != 0;   // 앱 글꼴 줄은 모양이 쪽 좌표로 구워져 있어 옮길 수 없다
                    if (it.Plan.OpCuts.Length == 1 && !laterApp && !priorAfter && !appNeedsShift && scale > 1e-9 && it.Plan.NewLengthPt > 0)
                    {
                        double newAdv = it.Plan.NewLengthPt / (tz * scale);
                        double oldAdv = cuts.Sum(c => c.AdvanceText);
                        if (Math.Abs(newAdv - oldAdv) > 1e-6)
                        {
                            var b2 = cuts.ToBuilder();
                            for (int ci = 0; ci < b2.Count; ci++) b2[ci] = b2[ci] with { AdvanceText = ci == 0 ? newAdv : 0 };
                            cutsOut = b2.ToImmutable();
                            delta = newAdv - oldAdv;
                            shifts.Add((last, delta));
                        }
                    }
                }
                spans.Add(new TextSpanReplacement(cutsOut, bytes, tm, appIndex, screenId, delta));
            }
            // 화면 조각: 글자를 읽기 순서로 훑으며 남는 글자 덩어리와 새 글을 번갈아 만든다.
            var m0 = ctx.SrcMatrix;
            PdfMatrix At(GlyphInfo g)
            {
                double sh = ShiftTextBefore((g.Str, g.Code));
                double dx = 0, dy = 0;
                if (sh != 0)
                {
                    double sc = Math.Sqrt(g.Place.A * g.Place.A + g.Place.B * g.Place.B), pt = sh * (g.Tz == 0 ? 1 : g.Tz / 100.0) * sc, th = g.Angle * Math.PI / 180;
                    dx = Math.Cos(th) * pt; dy = Math.Sin(th) * pt;
                }
                return new(m0.A, m0.B, m0.C, m0.D, (float)(g.Place.E + dx), (float)(g.Place.F + dy));
            }
            var run = new System.Text.StringBuilder(); GlyphInfo? runFirst = null;
            void FlushRun()
            {
                if (runFirst is not null && run.ToString().Trim().Length > 0) screen.Add(new ScreenTextPiece(s.Content.NewId(), run.ToString(), At(runFirst)));
                run.Clear(); runFirst = null;
            }
            foreach (var g in glyphs.OrderBy(g => g.Str).ThenBy(g => g.Code))
            {
                if (removed.Contains((g.Str, g.Code)))
                {
                    FlushRun();
                    if (newAt.TryGetValue((g.Str, g.Code), out var it) && it.Plan.NewText.Length > 0)
                        screen.Add(new ScreenTextPiece(it.ScreenId, it.Plan.NewText, At(g)));
                    continue;
                }
                runFirst ??= g;
                run.Append(g.Text);
            }
            FlushRun();
            foreach (var pe in priors)
                foreach (var pc in pe.Screen)
                    if (pe.Spans.Any(sp => sp.ScreenLineId == pc.LineId)) screen.Add(new ScreenTextPiece(s.Content.NewId(), pc.Text, pc.Placement));

            // 비교에서 뺄 영역: 이 글 객체 전체 + 새 글이 차지할 곳
            var b = TextMatcher.BoundsOf(glyphs);
            double l = b.L, bt = b.B, r = b.R, t = b.T;
            foreach (var (it, _) in members.Where(x => x.Item.FirstOp == op))
            {
                var f = it.Plan.Match.Glyphs[0];
                double th = f.Angle * Math.PI / 180, ex = f.X0 + Math.Cos(th) * it.Plan.NewLengthPt, ey = f.Y0 + Math.Sin(th) * it.Plan.NewLengthPt, pad = f.Size;
                l = Math.Min(l, Math.Min(f.X0, ex) - pad); r = Math.Max(r, Math.Max(f.X0, ex) + pad);
                bt = Math.Min(bt, Math.Min(f.Y0, ey) - pad); t = Math.Max(t, Math.Max(f.Y0, ey) + pad);
            }
            if (shifts.Count > 0) { var g1 = glyphs[0]; double padS = shifts.Sum(x => Math.Abs(x.Text)) * (g1.Tz == 0 ? 1 : g1.Tz / 100.0) * Math.Sqrt(g1.Place.A * g1.Place.A + g1.Place.B * g1.Place.B); l -= padS; r += padS; bt -= padS; t += padS; }
            foreach (var pe in priors) { l = Math.Min(l, pe.MaskRect.Left); bt = Math.Min(bt, pe.MaskRect.Bottom); r = Math.Max(r, pe.MaskRect.Right); t = Math.Max(t, pe.MaskRect.Top); }
            var mask = new PdfRectF((float)(l - 2), (float)(bt - 2), (float)(r + 2), (float)(t + 2));
            list.Add((page, new ReplaceTextSpanEdit(s.Content.NewId(), ObjectRef.Original(ctx.ObjectIndex), glyphs[0].Font!.Unit, spans.ToImmutable(), screen.ToImmutable(),
                appLines.ToImmutable(), appIds.ToImmutable(), mask)));
        }
        return list.Count == 0 ? null : new ContentEditBatchCommand(list);
    }
}
