using Pdf.Native;

namespace PdfEditor.Document.Output;

/// <summary>
/// 고른 쪽만 새 PDF로 꺼낸다(썸네일 "페이지 추출", 2026-10-20 사용자 요청). 살아 있는 문서를 직접 내보내지 않고
/// 저장과 같은 출력 사본(OutputCopyBuilder: 주석·쪽 객체 편집 반영)을 만든 뒤 남길 쪽만 두고 저장한다(설계서 4.2-6).
/// 암호 문서는 사본도 같은 암호로 남는다(PDFium 저장이 암호화를 유지, V6).
/// </summary>
public static class PageExtractor
{
    public static async Task ExtractAsync(DocumentSession s, OutputCopyBuilder output, IReadOnlyCollection<int> pageIndices,
        string targetPath, CancellationToken ct = default)
    {
        if (pageIndices.Count == 0) throw new ArgumentException("No pages to extract.", nameof(pageIndices));
        string dir = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        string tmp = Path.Combine(dir, "~" + Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await output.BuildAsync(s, tmp, ct).ConfigureAwait(false);
            var keep = new HashSet<int>(pageIndices);
            string? pw = s.Password;
            await s.Thread.InvokeAsync(() =>
            {
                using var copy = PdfDocument.Open(s.Thread, tmp, pw);
                if (keep.Any(i => i < 0 || i >= copy.PageCount)) throw new ArgumentOutOfRangeException(nameof(pageIndices));
                for (int i = copy.PageCount - 1; i >= 0; i--)
                    if (!keep.Contains(i)) copy.DeletePage(i);
                copy.SaveCopy(tmp);   // 사본은 메모리로 열었으므로 같은 경로에 다시 써도 된다
            }, ct).ConfigureAwait(false);
            File.Move(tmp, targetPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
        }
    }
}
