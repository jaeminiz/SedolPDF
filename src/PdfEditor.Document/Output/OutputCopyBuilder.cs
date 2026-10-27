using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;

namespace PdfEditor.Document.Output;

/// <summary>사본을 만든 결과. ExpectedAnnotCounts: 쪽별 주석 수(패치 반영), 검증에 쓴다.</summary>
/// <param name="HiddenTextCount">지운 원본 글 중 연산자째 지우지 못하고 보이지 않는 글자(3 Tr)로 남긴 수(M5 C12) — 0보다 크면 옛 글이 검색으로 나올 수 있다고 알린다.</param>
public sealed record OutputCopy(int[] ExpectedAnnotCounts, bool ContentApplied, bool Patched, int HiddenTextCount = 0);

/// <summary>
/// 파일을 만드는 모든 경로(저장·자동 복구·인쇄·요약)가 거치는 한 곳(M4 계획 C13, 설계서 4.2-6).
/// ① (전용 스레드) 주석 Flush + 패치 계획 + SaveCopy ② 객체 편집이 있으면 (전용 스레드) 사본을 열어 편집한 쪽마다
/// 편집 기록을 적용하고 GenerateContent 후 같은 경로에 다시 저장(암호 문서도 됨, V6) ③ 패치 가능하고 계획이 있으면 PDFsharp 패치.
/// 편집 중인 문서의 내용은 바꾸지 않는다(C1) — 같은 모델이면 몇 번을 만들어도 결과가 같다.
/// </summary>
public sealed class OutputCopyBuilder(IAppearanceSource? appearance, IAnnotationPatcher? patcher, AnnotationAssets? assets)
{
    /// <summary>시험용: PDFium 다시 쓰기를 건너뛰고 편집한 모든 쪽을 내용 스트림 고쳐 쓰기로 확정한다(패치 가능한 문서에서만).</summary>
    public bool ForceContentRewrite { get; init; }

    /// <summary>언제나 path에 사본을 만든다(저장·복구). onCopied: SaveCopy가 끝난 직후(그 뒤의 실패는 "후처리" 단계).</summary>
    public async Task<OutputCopy> BuildAsync(DocumentSession s, string path, CancellationToken ct = default, Action? onCopied = null)
        => (await BuildCoreAsync(s, path, always: true, ct, onCopied).ConfigureAwait(false))!;

    /// <summary>
    /// 살아 있는 문서를 그대로 그려도 되면(객체 편집 없음, 패치할 것 없음) 주석만 Flush하고 null.
    /// 아니면 path에 사본을 만든다(인쇄·요약).
    /// </summary>
    public Task<OutputCopy?> BuildIfNeededAsync(DocumentSession s, string path, CancellationToken ct = default)
        => BuildCoreAsync(s, path, always: false, ct, null);

    private async Task<OutputCopy?> BuildCoreAsync(DocumentSession s, string path, bool always, CancellationToken ct, Action? onCopied)
    {
        var (counts, plan, edited) = await s.Thread.InvokeAsync(() =>
        {
            // Flush는 트랜잭션이 아니다: 예외가 나면 SaveCopy 없이 그대로 던진다(원본·기존 사본 그대로).
            var p = s.FlushIfNeeded(appearance);
            var e = s.Content.EditedPages.Select(id => (Index: s.IndexOf(id), Edits: s.Content.For(id)))
                .Where(x => x.Index >= 0 && x.Edits.Length > 0).ToList();
            bool patch = !p.IsEmpty && SavePolicy.CanPatch(s);
            // 앱 글꼴 줄은 패치 단계만 쓸 수 있다. 패치를 못 하는(암호) 문서에서 조용히 버리지 않는다(M3a A2와 같은 원칙).
            if (!SavePolicy.CanPatch(s) && e.Any(x => x.Edits.OfType<InsertAppTextEdit>().Any() || x.Edits.OfType<ReplaceParagraphEdit>().Any(pe => !pe.AppLines.IsDefaultOrEmpty)))
                throw new InvalidOperationException("App-font text lines need the patch stage, which is not available for encrypted documents.");
            if (!always && e.Count == 0 && !patch) return ((int[]?)null, p, e);
            s.Document.SaveCopy(path);
            return ((int[]?)CollectAnnotCounts(s.Document), p, e);
        }, ct).ConfigureAwait(false);
        if (counts is null) return null;
        onCopied?.Invoke();

        bool content = false;
        RewriteWork? rewrite = null;
        if (edited.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string? pw = s.Password;
            bool canRewrite = patcher is not null && SavePolicy.CanPatch(s);
            rewrite = await s.Thread.InvokeAsync(() =>
            {
                // 사본은 메모리로 읽어 열므로(PdfDocument.Open) 같은 경로에 다시 써도 된다.
                return RegenerateContent(s, path, pw, edited, canRewrite, ForceContentRewrite, patcher as IContentRewriteProbe, assets ?? AnnotationAssets.Empty);
            }, ct).ConfigureAwait(false);
            content = true;
        }

        // PDFium이 못 다시 쓰는 쪽(M4x): 객체 편집을 내용 스트림 직접 고쳐 쓰기로 확정한다. 그 쪽의 앱 글꼴 줄은 고쳐 쓰기가 함께 쓴다.
        if (rewrite is { Rewrites.Length: > 0 })
        {
            var pages = rewrite.Rewrites.Select(r => r.PageIndex).ToHashSet();
            plan = plan with
            {
                ContentAppends = [.. plan.ContentAppends.Where(a => !pages.Contains(a.PageIndex))],
                ContentRewrites = rewrite.Rewrites,
            };
        }

        bool patched = false;
        if (!plan.IsEmpty && SavePolicy.CanPatch(s))
        {
            if (patcher is null) throw new InvalidOperationException("Annotation patch plan needs an IAnnotationPatcher.");
            ct.ThrowIfCancellationRequested();
            string? pw = s.Password;
            await Task.Run(() => patcher.Apply(path, pw, plan, assets ?? AnnotationAssets.Empty), ct).ConfigureAwait(false);
            for (int i = 0; i < counts.Length; i++) counts[i] += plan.CountDelta(i);
            patched = true;
        }

        // 고쳐 쓴 쪽도 같은 충실도 보호: 화면에 보이던 모양(메모리) ≡ 저장한 모양.
        if (rewrite is { Rewrites.Length: > 0 })
        {
            string? pw = s.Password;
            var changed = await s.Thread.InvokeAsync(() => FindChangedPages(s.Thread, path, pw, rewrite.Look, rewrite.Masks), ct).ConfigureAwait(false);
            if (changed.Count > 0) throw new ContentRegenerationException(changed[0] + 1, "content-stream rewrite changed the look");
        }
        return new OutputCopy(counts, content, patched, patched ? (patcher as IContentRewriteReport)?.HiddenTextCount ?? 0 : 0);
    }

    private const double GuardMaxSide = 640;
    private const double GuardMaxDiffRatio = 0.001;
    private const int GuardBlock = 8, GuardColorTolerance = 64, GuardInkLevel = 160, GuardInkBlockDiff = 16;

    /// <summary>
    /// 8x8 블록으로 비교한다. 블록이 다르다 = ① 평균색이 크게 다르다(면이 사라지거나 검게/회색으로 변함) 또는 ② 글·선 "잉크"(어두운 픽셀) 수가 크게 다르다(글자 깨짐).
    /// 색이 조금 달라지는 것(CMYK → RGB 근사 등)과 1픽셀 미만의 위치 반올림은 같다고 본다.
    /// </summary>
    private static int DifferentBlocks(byte[] a, byte[] b, int w, int h, out int blocks)
    {
        int bw = (w + GuardBlock - 1) / GuardBlock, bh = (h + GuardBlock - 1) / GuardBlock, diff = 0;
        blocks = bw * bh;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                long sa0 = 0, sa1 = 0, sa2 = 0, sb0 = 0, sb1 = 0, sb2 = 0; int cnt = 0, inkA = 0, inkB = 0;
                for (int y = by * GuardBlock; y < Math.Min(h, (by + 1) * GuardBlock); y++)
                    for (int x = bx * GuardBlock; x < Math.Min(w, (bx + 1) * GuardBlock); x++)
                    {
                        int i = (y * w + x) * 4;
                        sa0 += a[i]; sa1 += a[i + 1]; sa2 += a[i + 2]; sb0 += b[i]; sb1 += b[i + 1]; sb2 += b[i + 2]; cnt++;
                        if ((a[i] + a[i + 1] + a[i + 2]) / 3 < GuardInkLevel) inkA++;
                        if ((b[i] + b[i + 1] + b[i + 2]) / 3 < GuardInkLevel) inkB++;
                    }
                if (Math.Abs(sa0 - sb0) / cnt > GuardColorTolerance || Math.Abs(sa1 - sb1) / cnt > GuardColorTolerance || Math.Abs(sa2 - sb2) / cnt > GuardColorTolerance
                    || Math.Abs(inkA - inkB) > GuardInkBlockDiff) diff++;
            }
        return diff;
    }

    /// <summary>편집이 적용된 쪽을 작게 그린다(주석 제외 — 내용 스트림만 비교). 다시 쓰기 전후 비교용.</summary>
    private static byte[] RenderGuard(PdfPage page, out int w, out int h)
    {
        var size = page.SizePt;
        double scale = Math.Min(1.0, GuardMaxSide / Math.Max(1, Math.Max(size.Width, size.Height)));
        w = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
        h = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        return page.RenderTile(scale, 0, 0, w, h, false);
    }

    /// <summary>
    /// 충실도 보호(M4 마무리 검토, V13 확대). PDFium의 쪽 내용 다시 쓰기(GenerateContent)는 ① 셰이딩(sh)·패턴 색·이미지 마스크 색을 쓰지 못하고
    /// ② ICCBased·CalRGB·Separation 색을 잘못 쓰며(→ 도형 색 정규화로 피함) ③ 글 객체의 색을 다시 지정하면 글자 간격(Tc/Tw)이 있는 글이 깨지는 PDF가 있다(실제 PDF에서 확인).
    /// 그래서 편집을 적용한 쪽의 모양(메모리)을 기억해 두고, 다시 쓴 파일을 열어 비교한다. ① 색을 건드리지 않고 → 다른 쪽만 ② 도형 색 정규화 → 그래도 다른 쪽만 ③ 도형·글 색 정규화로 다시 쓴다(쪽마다 가장 덜 건드리는 방식).
    /// 그래도 다르면 조용히 망가진 파일을 내지 않고 실패시킨다.
    /// </summary>
    private sealed record RewriteWork(ImmutableArray<PageContentRewrite> Rewrites, Dictionary<int, (byte[] Pixels, int W, int H)> Look,
        Dictionary<int, MaskInfo>? Masks = null);

    /// <summary>비교에서 뺄 영역(쪽 공간 사각형 L,B,R,T)과 쪽 정보. 화면의 PDFium 글 객체가 자간·양쪽 맞춤 늘임을 못 그리는 문단 줄만 뺀다(M5 Task 5 한계 1).</summary>
    private sealed record MaskInfo(List<(double L, double B, double R, double T)> Rects, int Rotation, double W0, double H0, double Scale);

    private static RewriteWork RegenerateContent(DocumentSession s, string path, string? pw,
        IReadOnlyList<(int Index, ImmutableArray<ContentEdit> Edits)> edited, bool canRewrite, bool force,
        IContentRewriteProbe? probe, AnnotationAssets assets)
    {
        var look = new Dictionary<int, (byte[] Pixels, int W, int H)>();
        var level = edited.ToDictionary(x => x.Index, _ => 0);   // 0 = 색을 건드리지 않음, 1 = 도형 색 정규화, 2 = 도형·글 색 정규화
        var rewritePages = force && canRewrite ? edited.Select(x => x.Index).ToHashSet() : new HashSet<int>();   // PDFium이 못 다시 쓰는 쪽 → 내용 스트림 고쳐 쓰기(M4x)
        string gen = path + ".gen";
        try
        {
            // 문단 편집(M5 C7): 문단 편집이 있는 쪽은 내용 스트림 직접 쓰기를 먼저 해 본다(파일을 바꾸지 않는 시험 적용).
            // 객체 번호 대응·글꼴·상태 문제로 거절된 쪽만 PDFium 경로(줄마다 글 객체 + 충실도 보호)로 내려간다. 거절 이유는 둘 다 안 될 때 저장 거절 문구에 쓴다.
            var refCache = new Dictionary<int, ImmutableArray<ReferenceObj>>();   // 문단 편집 쪽은 시험 적용과 실제 고쳐 쓰기 사이에 객체 경계 목록을 두 번 읽지 않는다
            var rejected = new Dictionary<int, string>();
            var paraPages = edited.Where(x => x.Edits.OfType<ReplaceParagraphEdit>().Any()).Select(x => x.Index).Except(rewritePages).OrderBy(i => i).ToList();
            if (paraPages.Count > 0 && canRewrite)
            {
                var candidates = ReadRewrites(s, path, pw, edited.Where(x => paraPages.Contains(x.Index)).OrderBy(x => x.Index).ToList(), out _, null, refCache);
                var bad = probe?.CheckContentRewrites(path, pw, candidates, assets) ?? paraPages.ToDictionary(i => i, _ => "no content-rewrite probe");
                foreach (var i in paraPages)
                    if (bad.TryGetValue(i, out var why)) rejected[i] = why; else rewritePages.Add(i);
            }
            else foreach (var i in paraPages) rejected[i] = "content-stream rewrite not available";

            for (int pass = 0; ; pass++)
            {
                var active = edited.Where(x => !rewritePages.Contains(x.Index)).ToList();
                if (active.Count == 0) break;
                using (var copy = PdfDocument.Open(s.Thread, path, pw))
                {
                    foreach (var (index, edits) in active)
                    {
                        using var page = copy.LoadPage(index);
                        ContentReplayer.Apply(page, edits, ReplayMode.OutputPdfium, s.Assets);
                        if (pass == 0) look[index] = (RenderGuard(page, out int w, out int h), w, h);
                        if (level[index] > 0) ContentReplayer.NormalizeColors(page, includeText: level[index] > 1);
                        page.GenerateContent();
                    }
                    copy.SaveCopy(gen);
                }
                var failed = FindChangedPages(s.Thread, gen, pw, active.ToDictionary(x => x.Index, x => look[x.Index]));
                if (failed.Count == 0) { File.Move(gen, path, true); break; }
                var retry = failed.Where(i => level[i] < 2).ToList();
                if (retry.Count == 0)
                {
                    // 문단 편집 쪽: 직접 쓰기도 거절됐고 PDFium 경로도 모양이 달라지면 둘 다 안 되는 것 — 이유와 함께 저장 거절.
                    var both = failed.FirstOrDefault(i => rejected.ContainsKey(i), -1);
                    if (both >= 0) throw new ContentRegenerationException(both + 1, "paragraph edit: direct text writing was rejected (" + rejected[both] + ") and the PDFium path changed the look");
                    if (!canRewrite) throw new ContentRegenerationException(failed[0] + 1);
                    foreach (var i in failed) rewritePages.Add(i);
                    continue;
                }
                foreach (var i in retry) level[i]++;
            }

            if (rewritePages.Count == 0) return new RewriteWork([], look);

            // 고쳐 쓸 쪽: 사본(그 쪽은 PDFium이 건드리지 않은 원본 내용)에서 PDFium 객체 목록을 읽고, 화면에 보이는 모양(앱 글꼴 줄 포함)을 기억한다.
            var screenLook = new Dictionary<int, (byte[] Pixels, int W, int H)>();
            var rewrites = ReadRewrites(s, path, pw, edited.Where(x => rewritePages.Contains(x.Index)).OrderBy(x => x.Index).ToList(), out var masks, screenLook, refCache);
            return new RewriteWork(rewrites, screenLook, masks.Count > 0 ? masks : null);
        }
        finally { try { File.Delete(gen); } catch { } }
    }

    /// <summary>
    /// 고쳐 쓸 쪽들의 편집 기록 + 사본의 편집 전 PDFium 객체 목록을 모은다. screenLook이 있으면 화면 replay를 적용해 그 모양도 기억한다(저장 뒤 비교용).
    /// 이때 문단 줄 중 화면의 PDFium 글 객체가 자간·가로 비율·양쪽 맞춤 늘임을 못 그리는 줄은 비교에서 뺄 영역(masks)으로 돌려준다.
    /// </summary>
    private static ImmutableArray<PageContentRewrite> ReadRewrites(DocumentSession s, string path, string? pw,
        IReadOnlyList<(int Index, ImmutableArray<ContentEdit> Edits)> pages, out Dictionary<int, MaskInfo> masks,
        Dictionary<int, (byte[] Pixels, int W, int H)>? screenLook = null, Dictionary<int, ImmutableArray<ReferenceObj>>? refCache = null)
    {
        masks = [];
        var rewrites = ImmutableArray.CreateBuilder<PageContentRewrite>();
        using var copy = PdfDocument.Open(s.Thread, path, pw);
        foreach (var (index, edits) in pages)
        {
            using var page = copy.LoadPage(index);
            ImmutableArray<ReferenceObj> refList;
            if (refCache is not null && refCache.TryGetValue(index, out var cachedRefs)) refList = cachedRefs;
            else
            {
                var refs = ImmutableArray.CreateBuilder<ReferenceObj>(page.ObjectCount);
                for (int i = 0; i < page.ObjectCount; i++)
                {
                    var o = page.GetObject(i);
                    var b = o.Bounds;
                    refs.Add(new ReferenceObj((int)o.Type, b.Left, b.Bottom, b.Right, b.Top));
                }
                refList = refs.MoveToImmutable();
                if (refCache is not null) refCache[index] = refList;
            }
            if (screenLook is not null)
            {
                ContentReplayer.Apply(page, edits, ReplayMode.Screen, s.Assets);
                screenLook[index] = (RenderGuard(page, out int w, out int h), w, h);
                var rects = ParagraphMasks(edits);
                if (rects.Count > 0)
                {
                    var size = page.SizePt;
                    int rot = page.Rotation & 3;
                    bool odd = rot % 2 != 0;
                    masks[index] = new MaskInfo(rects, rot, odd ? size.Height : size.Width, odd ? size.Width : size.Height,
                        Math.Min(1.0, GuardMaxSide / Math.Max(1, Math.Max(size.Width, size.Height))));
                }
            }
            rewrites.Add(new PageContentRewrite(index, edits, refList));
        }
        return rewrites.ToImmutable();
    }

    /// <summary>자간·가로 비율·양쪽 맞춤 늘임이 있는 문단 줄 주변(쪽 공간). 줄 한 개 높이 띠, 가로로는 문단 상자 폭만큼 양쪽으로.</summary>
    private static List<(double L, double B, double R, double T)> ParagraphMasks(ImmutableArray<ContentEdit> edits)
    {
        var list = new List<(double, double, double, double)>();
        foreach (var pe in edits.OfType<ReplaceParagraphEdit>())
            foreach (var ln in pe.OriginalFontLines)
            {
                if (Math.Abs(ln.JustifyExtra) < 1e-6f && Math.Abs(ln.Tc) < 1e-6f && Math.Abs(ln.Tz - 100) < 1e-3f) continue;
                double reach = Math.Max(pe.Meta.Box.Width, pe.Meta.Box.Height) + ln.SizePt, band = 1.5 * ln.SizePt;
                bool horizontal = Math.Abs(ln.Placement.A) >= Math.Abs(ln.Placement.B);
                double e = ln.Placement.E, f = ln.Placement.F;
                list.Add(horizontal ? (e - reach, f - band, e + reach, f + band) : (e - band, f - reach, e + band, f + reach));
            }
        return list;
    }

    /// <summary>쪽 공간 사각형을 화면 그림(회전 반영, 왼쪽 위 원점) 좌표로 옮겨 두 그림에서 같은 색으로 칠한다.</summary>
    private static void ApplyMask(byte[] px, int w, int h, MaskInfo m)
    {
        foreach (var (l, b, r, t) in m.Rects)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var (x, y) in new[] { (l, b), (l, t), (r, b), (r, t) })
            {
                var (dx, dy) = m.Rotation switch
                {
                    1 => (y, x),
                    2 => (m.W0 - x, y),
                    3 => (m.H0 - y, m.W0 - x),
                    _ => (x, m.H0 - y),
                };
                x0 = Math.Min(x0, dx * m.Scale); x1 = Math.Max(x1, dx * m.Scale);
                y0 = Math.Min(y0, dy * m.Scale); y1 = Math.Max(y1, dy * m.Scale);
            }
            int ix0 = Math.Max(0, (int)Math.Floor(x0)), ix1 = Math.Min(w, (int)Math.Ceiling(x1)), iy0 = Math.Max(0, (int)Math.Floor(y0)), iy1 = Math.Min(h, (int)Math.Ceiling(y1));
            for (int y = iy0; y < iy1; y++)
                for (int x = ix0; x < ix1; x++)
                {
                    int i = (y * w + x) * 4;
                    px[i] = px[i + 1] = px[i + 2] = 255;
                }
        }
    }

    private static List<int> FindChangedPages(PdfiumThread t, string file, string? pw, Dictionary<int, (byte[] Pixels, int W, int H)> look,
        Dictionary<int, MaskInfo>? masks = null)
    {
        var failed = new List<int>();
        using var check = PdfDocument.Open(t, file, pw);
        foreach (var (index, (pixels0, w, h)) in look)
        {
            using var page = check.LoadPage(index);
            var size = page.SizePt;
            double scale = Math.Min(1.0, GuardMaxSide / Math.Max(1, Math.Max(size.Width, size.Height)));
            var actual = page.RenderTile(scale, 0, 0, w, h, false);
            var pixels = pixels0;
            if (masks is not null && masks.TryGetValue(index, out var mask))
            {
                pixels = (byte[])pixels0.Clone();
                ApplyMask(pixels, w, h, mask);
                ApplyMask(actual, w, h, mask);
            }
            int diff = DifferentBlocks(pixels, actual, w, h, out int blocks);
            if (diff > Math.Max(2, blocks * GuardMaxDiffRatio)) failed.Add(index);
        }
        return failed;
    }

    private static int[] CollectAnnotCounts(PdfDocument doc)
    {
        int n = doc.PageCount;
        var counts = new int[n];
        for (int i = 0; i < n; i++)
        {
            using var page = doc.LoadPage(i);
            counts[i] = page.AnnotationCount;
        }
        return counts;
    }
}
