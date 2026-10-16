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
    private readonly List<int> _stashed = [];   // 실행 취소 상태에서 각 쪽이 있는 UndoStash 번호 (_ids와 같은 순서)

    public void Execute(DocumentSession s)
    {
        if (sourceIndices.Count == 0) throw new InvalidOperationException("가져올 페이지가 없습니다.");
        if (insertAt < 0 || insertAt > s.Pages.Length) throw new ArgumentOutOfRangeException(nameof(insertAt));
        if (originals is not null && originals.Count != sourceIndices.Count)
            throw new ArgumentException("originals must have one entry per imported page.", nameof(originals));
        if (_ids is not null)
        {
            // 다시 실행: UndoStash에 옮겨 둔 쪽을 같은 PageId로 되가져온다 (계획 D11)
            var stash = s.UndoStash;
            for (int k = 0; k < _stashed.Count; k++)
            {
                s.Document.ImportPages(stash, [_stashed[k]], insertAt + k);
                s.InsertPageIds(insertAt + k, [_ids[k]]);
            }
            // 스택은 LIFO이므로 이 명령의 쪽은 stash 끝에 있다. 뒤에서부터 지워 인덱스를 보존한다.
            foreach (int idx in _stashed.OrderByDescending(x => x)) stash.DeletePage(idx);
            _stashed.Clear();
            return;
        }
        s.Document.ImportPages(source, sourceIndices, insertAt);   // 실패하면 PDFium이 바꾸지 않음
        _ids = s.NewPageIds(sourceIndices.Count);
        s.InsertPageIds(insertAt, _ids);
        if (originals is not null)
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
        var stash = s.UndoStash;
        _stashed.Clear();
        // 문서 순서대로 UndoStash 끝에 옮긴다 (다시 실행 때 같은 순서로 되가져옴)
        foreach (var id in _ids!)
        {
            int i = s.IndexOf(id);
            int stashIdx = stash.PageCount;
            stash.ImportPages(s.Document, [i], stashIdx);
            s.Document.DeletePage(i);
            s.RemovePageIdAt(i);
            _stashed.Add(stashIdx);
        }
    }

    public void Dispose() => source.Dispose();
}
