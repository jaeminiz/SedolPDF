using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Commands;

namespace PdfEditor.Pages;

/// <summary>
/// 다른 문서의 쪽들을 insertAt에 가져온다. 원본 문서를 소유하며 Dispose에서 닫는다
/// (PdfDocument.Dispose가 PDFium 스레드로 보낸다). 다시 실행하면 같은 PageId를 쓴다.
/// originals[k]는 sourceIndices[k]쪽의 원본 주석이며, 처음 Execute에서 새 PageId에 붙인다(null이면 붙이지 않음).
/// </summary>
public sealed class ImportPagesCommand(PdfDocument source, IReadOnlyList<int> sourceIndices, int insertAt,
    IReadOnlyList<IReadOnlyList<OriginalAnnotation>>? originals = null)
    : IUndoableCommand, IDisposable
{
    private IReadOnlyList<PageId>? _ids;

    public void Execute(DocumentSession s)
    {
        if (sourceIndices.Count == 0) throw new InvalidOperationException("가져올 페이지가 없습니다.");
        if (insertAt < 0 || insertAt > s.Pages.Length) throw new ArgumentOutOfRangeException(nameof(insertAt));
        if (originals is not null && originals.Count != sourceIndices.Count)
            throw new ArgumentException("originals must have one entry per imported page.", nameof(originals));
        s.Document.ImportPages(source, sourceIndices, insertAt);   // 실패하면 PDFium이 바꾸지 않음
        bool first = _ids is null;
        _ids ??= s.NewPageIds(sourceIndices.Count);
        s.InsertPageIds(insertAt, _ids);
        if (first && originals is not null)
        {
            // 쪽은 이미 들어갔으므로 붙이다 실패해도 예외를 밖으로 내지 않는다 (다시 실행하면 쪽이 두 번 들어간다).
            // 원본 주석만 읽기 전용으로 내려간다.
            try
            {
                for (int k = 0; k < _ids.Count; k++)
                    s.AttachOriginalsVerified(_ids[k], originals[k]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                s.MarkOriginalAnnotationsReadOnly();
            }
        }
    }

    public void Undo(DocumentSession s)
    {
        foreach (var id in _ids!.OrderByDescending(s.IndexOf).ToList())
        {
            int i = s.IndexOf(id);
            s.Document.DeletePage(i);
            s.RemovePageIdAt(i);
        }
    }

    public void Dispose() => source.Dispose();
}
