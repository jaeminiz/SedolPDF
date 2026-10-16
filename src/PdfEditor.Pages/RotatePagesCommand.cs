using PdfEditor.Document;
using PdfEditor.Document.Commands;

namespace PdfEditor.Pages;

/// <summary>+1 = 시계 방향 90도, -1 = 반시계 방향 90도.</summary>
public sealed class RotatePagesCommand : IUndoableCommand
{
    private readonly IReadOnlyList<PageId> _pages;
    private readonly int _q;
    private readonly Dictionary<PageId, int> _old = new();

    public RotatePagesCommand(IReadOnlyList<PageId> pages, int quarterTurns)
    {
        if (pages.Count == 0) throw new ArgumentException("회전할 페이지가 없습니다.", nameof(pages));
        _pages = pages;
        _q = quarterTurns;
    }

    public void Execute(DocumentSession s)
    {
        _old.Clear();
        var done = new List<PageId>();
        try
        {
            foreach (var id in _pages.Distinct())
            {
                // 핸들은 다음 GetLoadedPage 전에 놓는다 (캐시가 닫을 수 있음)
                var page = s.GetLoadedPage(id);
                int old = page.Rotation;
                page.Rotation = (((old + _q) % 4) + 4) % 4;
                _old[id] = old;
                done.Add(id);
                s.BumpVersion(id);   // 크기가 바뀌므로 버전을 올리고 핸들을 닫는다
                s.Annotations.MarkAppearanceStale(id);   // 손댄 Text 아이콘은 다음 Flush에 다시 그린다
            }
        }
        catch
        {
            Restore(s, done);
            throw;
        }
    }

    public void Undo(DocumentSession s) => Restore(s, _old.Keys.ToList());

    private void Restore(DocumentSession s, List<PageId> ids)
    {
        foreach (var id in ids)
        {
            s.GetLoadedPage(id).Rotation = _old[id];
            s.BumpVersion(id);
            s.Annotations.MarkAppearanceStale(id);
        }
    }
}
