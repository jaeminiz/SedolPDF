using Pdf.Native;

namespace PdfEditor.Document.Saving;

/// <summary>전체 저장 → 같은 폴더 임시 파일 → 후처리 → 다시 열어 검증 → 교체. 실패하면 원본은 그대로.</summary>
public sealed class SaveService(IReadOnlyList<ISavePostProcessor> postProcessors)
{
    public async Task SaveAsync(DocumentSession s, string targetPath, CancellationToken ct = default)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        string tmp = Path.Combine(dir, "~" + Path.GetFileName(targetPath) + ".tmp");
        var stage = SaveStage.Write;
        try
        {
            // ① 쓰기 + 검증용 값 수집 (같은 전용 스레드 작업)
            int[] expected = await s.Thread.InvokeAsync(() =>
            {
                s.Document.SaveCopy(tmp);
                return CollectAnnotCounts(s.Document);
            }, ct).ConfigureAwait(false);

            // ② 후처리
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
        s.MarkSaved(targetPath);
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
