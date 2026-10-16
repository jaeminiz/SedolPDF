using Pdf.Native;
using PdfEditor.Document.Annotations.Reading;

namespace PdfEditor.Pages;

/// <summary>다른 파일에서 쪽을 가져오거나 병합할 때, 그 파일의 원본 주석을 읽는다.</summary>
public static class OriginalAnnotationLoader
{
    /// <summary>
    /// pageIndices의 쪽마다 원본 주석 목록(주석이 없는 쪽은 빈 목록)을 돌려준다. 읽을 수 없으면 null
    /// (그 쪽들의 원본 주석은 읽기 전용). 주석 수는 일반 줄의 PDFium으로 세고, 0개면 PDFsharp를 열지 않는다.
    /// </summary>
    public static async Task<IReadOnlyList<IReadOnlyList<OriginalAnnotation>>?> ReadAsync(
        PdfiumThread t, PdfDocument doc, byte[] bytes, string? password, IReadOnlyList<int> pageIndices)
    {
        try
        {
            var withAnnots = await t.InvokeAsync(() =>
            {
                var r = new List<int>();
                for (int k = 0; k < pageIndices.Count; k++)
                {
                    using var p = doc.LoadPage(pageIndices[k]);
                    if (p.AnnotationCount > 0) r.Add(k);
                }
                return r;
            }).ConfigureAwait(false);

            var result = new IReadOnlyList<OriginalAnnotation>[pageIndices.Count];
            Array.Fill(result, Array.Empty<OriginalAnnotation>());
            if (withAnnots.Count == 0) return result;

            var read = await Task.Run(() => AnnotationReader.Read(bytes, password, withAnnots.Select(k => pageIndices[k]).ToArray()))
                .ConfigureAwait(false);
            for (int i = 0; i < withAnnots.Count; i++) result[withAnnots[i]] = read[i];
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }   // 어떤 실패든 읽기 전용으로 내려간다
    }
}
