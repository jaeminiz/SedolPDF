using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;

namespace PdfEditor.Document.Saving;

/// <summary>전체 저장 → 같은 폴더 임시 파일 → 후처리 → 다시 열어 검증 → 교체. 실패하면 원본은 그대로.</summary>
public sealed class SaveService(
    IReadOnlyList<ISavePostProcessor> postProcessors,
    IAppearanceSource? appearance = null,
    IAnnotationPatcher? patcher = null,
    AnnotationAssets? assets = null)
{
    /// <summary>마지막 저장에서 지운 원본 글 중 연산자째 지우지 못해 보이지 않는 글자로 남긴 수(M5 C12). 0보다 크면 옛 글이 검색에 나올 수 있다고 알린다.</summary>
    public int LastHiddenTextCount { get; private set; }

    public async Task SaveAsync(DocumentSession s, string targetPath, CancellationToken ct = default)
    {
        LastHiddenTextCount = 0;
        string dir = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        string tmp = Path.Combine(dir, "~" + Path.GetFileName(targetPath) + ".tmp");
        var stage = SaveStage.Write;
        try
        {
            // ① 쓰기(주석 Flush + SaveCopy) ② 객체 편집 확정 + 패치(PDFsharp) — 모두 임시 파일에만 (OutputCopyBuilder, M4)
            var output = new Output.OutputCopyBuilder(appearance, patcher, assets);
            // 사본을 쓴 뒤(내용 확정·패치)의 실패는 PostProcess 단계로 알린다.
            var built = await output.BuildAsync(s, tmp, ct, onCopied: () => stage = SaveStage.PostProcess).ConfigureAwait(false);
            int[] expected = built.ExpectedAnnotCounts;
            LastHiddenTextCount = built.HiddenTextCount;

            stage = SaveStage.PostProcess;
            foreach (var pp in postProcessors)
            {
                ct.ThrowIfCancellationRequested();
                if (pp.AppliesTo(s)) pp.Process(tmp, s.Password);
            }

            // ③ 다시 열어 검증
            stage = SaveStage.Verify;
            string? pw = s.Password;
            await s.Thread.InvokeAsync(() =>
            {
                using var check = PdfDocument.Open(s.Thread, tmp, pw);
                var actual = CollectAnnotCounts(check);
                if (actual.Length != expected.Length)
                    throw new InvalidDataException($"쪽 수 불일치: 기대 {expected.Length}, 실제 {actual.Length}");
                for (int i = 0; i < actual.Length; i++)
                    if (actual[i] != expected[i])
                        throw new InvalidDataException($"{i + 1}쪽 주석 수 불일치: 기대 {expected[i]}, 실제 {actual[i]}");
            }, ct).ConfigureAwait(false);

            // ④ 교체
            stage = SaveStage.Replace;
            if (File.Exists(targetPath)) File.Replace(tmp, targetPath, null);
            else File.Move(tmp, targetPath);
        }
        catch (Exception e)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw new SaveException(stage, e);
        }
        // 저장한 파일의 해시를 새 "원본"으로 기록한다(복구 때 원본이 바뀌었는지 가리는 기준).
        byte[]? sha = null;
        try { sha = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(targetPath, ct).ConfigureAwait(false)); }
        catch (Exception) { /* 해시를 못 구해도 저장은 성공 */ }
        s.MarkSaved(targetPath, sha);
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
