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
                using var copy = PdfDocument.Open(s.Thread, path, pw);
                foreach (var (index, edits) in edited)
                {
                    using var page = copy.LoadPage(index);
                    ContentReplayer.Apply(page, edits, ReplayMode.Output, s.Assets);
                    ContentReplayer.NormalizeColors(page);
                    page.GenerateContent();
                }
                copy.SaveCopy(path);
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
