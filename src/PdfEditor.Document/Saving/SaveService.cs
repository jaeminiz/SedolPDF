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
    public async Task SaveAsync(DocumentSession s, string targetPath, CancellationToken ct = default)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        string tmp = Path.Combine(dir, "~" + Path.GetFileName(targetPath) + ".tmp");
        var stage = SaveStage.Write;
        try
        {
            // ① 쓰기 + 검증용 값 수집 (같은 전용 스레드 작업)
            var (flushed, plan) = await s.Thread.InvokeAsync(() =>
            {
                // Flush는 트랜잭션이 아니다: 예외가 나면 문서가 일부만 바뀌었을 수 있으므로 SaveCopy 없이 중단한다(원본은 그대로).
                var p = s.FlushIfNeeded(appearance);
                s.Document.SaveCopy(tmp);
                return (CollectAnnotCounts(s.Document), p);
            }, ct).ConfigureAwait(false);

            // ② 패치(PDFsharp, 임시 파일에만) + 후처리
            stage = SaveStage.PostProcess;
            int[] expected = (int[])flushed.Clone();
            if (!plan.IsEmpty && SavePolicy.CanPatch(s))
            {
                if (patcher is null)
                    throw new InvalidOperationException("Annotation patch plan needs an IAnnotationPatcher.");
                ct.ThrowIfCancellationRequested();
                patcher.Apply(tmp, s.Password, plan, assets ?? AnnotationAssets.Empty);
                for (int i = 0; i < expected.Length; i++) expected[i] += plan.CountDelta(i);
            }
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
