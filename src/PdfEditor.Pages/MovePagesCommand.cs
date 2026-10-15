using PdfEditor.Document;
using PdfEditor.Document.Commands;

namespace PdfEditor.Pages;

/// <summary>pages를 문서 순서대로 연속해서 놓고 첫 쪽의 결과 인덱스가 destIndex가 되게 옮긴다.</summary>
public sealed class MovePagesCommand : IUndoableCommand
{
    private readonly IReadOnlyList<PageId> _pages;
    private readonly int _destIndex;
    private PageId[]? _before;

    public MovePagesCommand(IReadOnlyList<PageId> pages, int destIndex)
    {
        if (pages.Count == 0) throw new ArgumentException("옮길 페이지가 없습니다.", nameof(pages));
        _pages = pages;
        _destIndex = destIndex;
    }

    public void Execute(DocumentSession s)
    {
        var order = s.Pages.Select(p => p.Id).ToArray();
        var set = new HashSet<PageId>(_pages);
        var moving = order.Where(set.Contains).ToArray();   // 문서 순서
        if (moving.Length != set.Count) throw new InvalidOperationException("문서에 없는 페이지가 포함되어 있습니다.");
        if (_destIndex < 0 || _destIndex > order.Length - moving.Length)
            throw new ArgumentOutOfRangeException(nameof(_destIndex));

        var indices = moving.Select(id => Array.IndexOf(order, id)).ToArray();
        _before = order;
        s.Document.MovePages(indices, _destIndex);   // 실패하면 PDFium이 바꾸지 않음 → 예외 전달
        s.MovePageIds(moving, _destIndex);
    }

    public void Undo(DocumentSession s) => PageOrder.ReorderTo(s, _before!);
}
