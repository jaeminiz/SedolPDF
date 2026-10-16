using Pdf.Native;
using PdfEditor.Document;

namespace PdfEditor.Pages;

public sealed record MergeSource(string Path, string? Password);

public static class MergeService
{
    /// <summary>
    /// 빈 새 문서에 원본마다 전체 쪽을 끝에 가져온다. 결과는 저장 안 된 새 세션(IsDirty).
    /// 원본의 책갈피는 가져오지 않는다 (PDFium 가져오기 한계 — 검증 필요: FPDF_ImportPagesByIndex가 책갈피를 옮기지 않음).
    /// 도중에 실패하면 만든 세션을 닫고 예외를 그대로 던진다.
    /// </summary>
    public static async Task<DocumentSession> CreateMergedAsync(
        PdfiumThread t, IReadOnlyList<MergeSource> sources, string displayName, CancellationToken ct)
    {
        if (sources.Count == 0) throw new ArgumentException("병합할 파일이 없습니다.", nameof(sources));

        var session = await DocumentSession.CreateEmptyAsync(t).ConfigureAwait(false);
        try
        {
            session.DisplayName = displayName;
            foreach (var src in sources)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = await File.ReadAllBytesAsync(src.Path, ct).ConfigureAwait(false);
                // 원본 문서는 주석을 읽는 동안 열어 두었다가 전용 스레드에서 닫는다.
                var (doc, ids) = await t.InvokeAsync(() =>
                {
                    var d = PdfDocument.OpenBytes(t, bytes, src.Password);
                    try
                    {
                        int n = d.PageCount;
                        if (n == 0) return (d, (IReadOnlyList<PageId>)[]);
                        int at = session.Pages.Length;
                        session.Document.ImportPages(d, Enumerable.Range(0, n).ToArray(), at);
                        var newIds = session.NewPageIds(n);
                        session.InsertPageIds(at, newIds);
                        session.Publish();
                        return (d, newIds);
                    }
                    catch { d.Dispose(); throw; }
                }, ct).ConfigureAwait(false);
                try
                {
                    if (ids.Count == 0) continue;
                    var read = await OriginalAnnotationLoader.ReadAsync(t, doc, bytes, src.Password,
                        Enumerable.Range(0, ids.Count).ToArray()).ConfigureAwait(false);
                    if (read is null) { session.MarkOriginalAnnotationsReadOnly(); continue; }
                    await t.InvokeAsync(() =>
                    {
                        for (int k = 0; k < ids.Count; k++) session.AttachOriginalsVerified(ids[k], read[k]);
                        session.Annotations.Publish();
                    }, ct).ConfigureAwait(false);
                }
                finally { await t.InvokeAsync(doc.Dispose).ConfigureAwait(false); }
            }
            session.MarkModified();
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
