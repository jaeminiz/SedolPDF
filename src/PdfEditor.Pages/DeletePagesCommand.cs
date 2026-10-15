using PdfEditor.Document;
using PdfEditor.Document.Commands;

namespace PdfEditor.Pages;

/// <summary>
/// 페이지를 지운다. 지우기 전에 실행 취소용 임시 문서(UndoStash)에 복사해 두고,
/// 취소하면 같은 PageId로 원래 인덱스에 되돌린다 (주석·회전 포함 — PDFium 가져오기 동작, 검증은 테스트).
/// </summary>
public sealed class DeletePagesCommand : IUndoableCommand
{
    private readonly IReadOnlyList<PageId> _pages;
    private readonly List<(PageId Id, int OrigIndex, int StashIndex)> _stashed = [];

    public DeletePagesCommand(IReadOnlyList<PageId> pages)
    {
        if (pages.Count == 0) throw new ArgumentException("지울 페이지가 없습니다.", nameof(pages));
        _pages = pages;
    }

    public void Execute(DocumentSession s)
    {
        var targets = _pages.Distinct()
            .Select(id => (Id: id, Index: s.IndexOf(id)))
            .ToList();
        if (targets.Any(t => t.Index < 0))
            throw new InvalidOperationException("문서에 없는 페이지가 포함되어 있습니다.");
        if (targets.Count >= s.Pages.Length)
            throw new InvalidOperationException("모든 페이지를 지울 수 없습니다.");

        _stashed.Clear();
        var stash = s.UndoStash;
        try
        {
            foreach (var (id, idx) in targets.OrderByDescending(t => t.Index))
            {
                int stashIdx = stash.PageCount;
                stash.ImportPages(s.Document, [idx], stashIdx);
                _stashed.Add((id, idx, stashIdx));
                s.Document.DeletePage(idx);
                s.RemovePageIdAt(idx);
            }
        }
        catch
        {
            RestoreAll(s);   // 이미 지운 것을 되돌린 뒤 예외 전달
            throw;
        }
    }

    public void Undo(DocumentSession s) => RestoreAll(s);

    private void RestoreAll(DocumentSession s)
    {
        var stash = s.UndoStash;
        // 원래 인덱스 오름차순: 앞쪽부터 끼우면 뒤쪽 원래 인덱스가 그대로 유효하다
        foreach (var (id, orig, stashIdx) in _stashed.OrderBy(x => x.OrigIndex))
        {
            s.Document.ImportPages(stash, [stashIdx], orig);
            s.InsertPageIds(orig, [id]);
        }
        // 스택은 LIFO이므로 이 명령의 쪽은 stash 끝에 있다. 뒤에서부터 지워 인덱스를 보존한다.
        foreach (var x in _stashed.OrderByDescending(x => x.StashIndex))
            stash.DeletePage(x.StashIndex);
        _stashed.Clear();
    }
}
