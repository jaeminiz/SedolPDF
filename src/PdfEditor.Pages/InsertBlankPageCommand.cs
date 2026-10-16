using PdfEditor.Document;
using PdfEditor.Document.Commands;

namespace PdfEditor.Pages;

/// <summary>
/// 빈 쪽을 삽입한다. 실행 취소는 쪽을 버리지 않고 UndoStash로 옮기고, 다시 실행은 stash에서 같은 PageId로 되가져온다
/// (저장·Flush 뒤에도 PDFium 쪽 주석과 모델이 어긋나지 않게, 계획 D11). 처음 Execute만 빈 쪽을 만든다.
/// </summary>
public sealed class InsertBlankPageCommand(int index, float widthPt, float heightPt) : IUndoableCommand
{
    private PageId? _id;
    private int _stashIndex = -1;   // 실행 취소 상태에서 쪽이 있는 UndoStash 번호

    public void Execute(DocumentSession s)
    {
        if (index < 0 || index > s.Pages.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (_id is null)
        {
            s.Document.InsertBlankPage(index, widthPt, heightPt);   // 실패하면 아무것도 안 바뀜
            _id = s.NewPageIds(1)[0];
        }
        else
        {
            var stash = s.UndoStash;
            s.Document.ImportPages(stash, [_stashIndex], index);
            stash.DeletePage(_stashIndex);
            _stashIndex = -1;
        }
        s.InsertPageIds(index, [_id.Value]);
    }

    public void Undo(DocumentSession s)
    {
        int i = s.IndexOf(_id!.Value);
        var stash = s.UndoStash;
        int stashIdx = stash.PageCount;
        stash.ImportPages(s.Document, [i], stashIdx);
        s.Document.DeletePage(i);
        s.RemovePageIdAt(i);
        _stashIndex = stashIdx;
    }
}
