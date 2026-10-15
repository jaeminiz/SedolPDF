using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Commands;

namespace PdfEditor.Pages;

/// <summary>
/// 다른 문서의 쪽들을 insertAt에 가져온다. 원본 문서를 소유하며 Dispose에서 닫는다
/// (PdfDocument.Dispose가 PDFium 스레드로 보낸다). 다시 실행하면 같은 PageId를 쓴다.
/// </summary>
public sealed class ImportPagesCommand(PdfDocument source, IReadOnlyList<int> sourceIndices, int insertAt)
    : IUndoableCommand, IDisposable
{
    private IReadOnlyList<PageId>? _ids;

    public void Execute(DocumentSession s)
    {
        if (sourceIndices.Count == 0) throw new InvalidOperationException("가져올 페이지가 없습니다.");
        if (insertAt < 0 || insertAt > s.Pages.Length) throw new ArgumentOutOfRangeException(nameof(insertAt));
        s.Document.ImportPages(source, sourceIndices, insertAt);   // 실패하면 PDFium이 바꾸지 않음
        _ids ??= s.NewPageIds(sourceIndices.Count);
        s.InsertPageIds(insertAt, _ids);
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
