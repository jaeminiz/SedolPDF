using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;

namespace PdfEditor.Document.Output;

/// <summary>사본을 만든 결과. ExpectedAnnotCounts: 쪽별 주석 수(패치 반영), 검증에 쓴다.</summary>
public sealed record OutputCopy(int[] ExpectedAnnotCounts, bool ContentApplied, bool Patched);

/// <summary>
/// 파일을 만드는 모든 경로(저장·자동 복구·인쇄·요약)가 거치는 한 곳(M4 계획 C13, 설계서 4.2-6).
/// ① (전용 스레드) 주석 Flush + 패치 계획 + SaveCopy ② 객체 편집이 있으면 (전용 스레드) 사본을 열어 편집한 쪽마다
/// 편집 기록을 적용하고 GenerateContent 후 같은 경로에 다시 저장(암호 문서도 됨, V6) ③ 패치 가능하고 계획이 있으면 PDFsharp 패치.
/// 편집 중인 문서의 내용은 바꾸지 않는다(C1) — 같은 모델이면 몇 번을 만들어도 결과가 같다.
/// </summary>
public sealed class OutputCopyBuilder(IAppearanceSource? appearance, IAnnotationPatcher? patcher, AnnotationAssets? assets)
{
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
            if (!SavePolicy.CanPatch(s) && e.Any(x => x.Edits.OfType<InsertAppTextEdit>().Any()))
                throw new InvalidOperationException("App-font text lines need the patch stage, which is not available for encrypted documents.");
            if (!always && e.Count == 0 && !patch) return ((int[]?)null, p, e);
            s.Document.SaveCopy(path);
            return ((int[]?)CollectAnnotCounts(s.Document), p, e);
        }, ct).ConfigureAwait(false);
        if (counts is null) return null;
        onCopied?.Invoke();

        bool content = false;
        if (edited.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string? pw = s.Password;
            await s.Thread.InvokeAsync(() =>
            {
                // 사본은 메모리로 읽어 열므로(PdfDocument.Open) 같은 경로에 다시 써도 된다.
                RegenerateContent(s, path, pw, edited);
            }, ct).ConfigureAwait(false);
            content = true;
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
        return new OutputCopy(counts, content, patched);
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
    private static void RegenerateContent(DocumentSession s, string path, string? pw, IReadOnlyList<(int Index, System.Collections.Immutable.ImmutableArray<ContentEdit> Edits)> edited)
    {
        var look = new Dictionary<int, (byte[] Pixels, int W, int H)>();
        var level = edited.ToDictionary(x => x.Index, _ => 0);   // 0 = 색을 건드리지 않음, 1 = 도형 색 정규화, 2 = 도형·글 색 정규화
        string gen = path + ".gen";
        try
        {
            for (int pass = 0; ; pass++)
            {
                using (var copy = PdfDocument.Open(s.Thread, path, pw))
                {
                    foreach (var (index, edits) in edited)
                    {
                        using var page = copy.LoadPage(index);
                        ContentReplayer.Apply(page, edits, ReplayMode.Output, s.Assets);
                        if (pass == 0) look[index] = (RenderGuard(page, out int w, out int h), w, h);
                        if (level[index] > 0) ContentReplayer.NormalizeColors(page, includeText: level[index] > 1);
                        page.GenerateContent();
                    }
                    copy.SaveCopy(gen);
                }
                var failed = FindChangedPages(s.Thread, gen, pw, look);
                if (failed.Count == 0) { File.Move(gen, path, true); return; }
                var retry = failed.Where(i => level[i] < 2).ToList();
                if (retry.Count == 0) throw new ContentRegenerationException(failed[0] + 1);
                foreach (var i in retry) level[i]++;
            }
        }
        finally { try { File.Delete(gen); } catch { } }
    }

    private static List<int> FindChangedPages(PdfiumThread t, string file, string? pw, Dictionary<int, (byte[] Pixels, int W, int H)> look)
    {
        var failed = new List<int>();
        using var check = PdfDocument.Open(t, file, pw);
        foreach (var (index, (pixels, w, h)) in look)
        {
            using var page = check.LoadPage(index);
            var size = page.SizePt;
            double scale = Math.Min(1.0, GuardMaxSide / Math.Max(1, Math.Max(size.Width, size.Height)));
            var actual = page.RenderTile(scale, 0, 0, w, h, false);
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
