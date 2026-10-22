using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Commands;

namespace PdfEditor.Document.Content;

/// <summary>
/// 쪽마다의 객체 편집 기록. 바꾸기는 PDFium 전용 스레드(명령)에서만, 읽기는 어느 스레드에서나(스냅숏).
/// 기록은 덧붙이기만 하고, 실행 취소는 같은 명령이 덧붙인 것을 뒤에서부터 뺀다(순서가 곧 적용 순서).
/// </summary>
public sealed class ContentEditStore
{
    private readonly PdfiumThread _thread;
    private readonly Dictionary<PageId, List<ContentEdit>> _pages = new();
    private ImmutableDictionary<PageId, ImmutableArray<ContentEdit>> _snapshot = ImmutableDictionary<PageId, ImmutableArray<ContentEdit>>.Empty;
    private long _nextId;

    internal ContentEditStore(PdfiumThread thread) => _thread = thread;

    /// <summary>새 편집 Id (어느 스레드에서나).</summary>
    public long NewId() => Interlocked.Increment(ref _nextId);

    /// <summary>그 쪽의 편집 기록(스냅숏, 적용 순서).</summary>
    public ImmutableArray<ContentEdit> For(PageId page) => _snapshot.TryGetValue(page, out var a) ? a : [];

    /// <summary>편집 기록이 있는 쪽들(스냅숏).</summary>
    public IEnumerable<PageId> EditedPages => _snapshot.Where(kv => kv.Value.Length > 0).Select(kv => kv.Key);

    public bool HasEdits => _snapshot.Values.Any(v => v.Length > 0);

    internal void Append(PageId page, ContentEdit edit)
    {
        _thread.AssertOnThread();
        if (!_pages.TryGetValue(page, out var list)) _pages[page] = list = [];
        list.Add(edit);
        Snap(page, list);
    }

    internal void RemoveLast(PageId page, ContentEdit edit)
    {
        _thread.AssertOnThread();
        if (!_pages.TryGetValue(page, out var list) || list.Count == 0 || !ReferenceEquals(list[^1], edit))
            throw new InvalidOperationException("Content edit log is out of order (undo of a command that is not the latest on this page).");
        list.RemoveAt(list.Count - 1);
        Snap(page, list);
    }

    /// <summary>기록 가운데 edit 하나를 빼고 그 자리(index)를 돌려준다(문단 다시 편집이 이전 편집을 대체할 때).</summary>
    internal int Remove(PageId page, ContentEdit edit)
    {
        _thread.AssertOnThread();
        if (!_pages.TryGetValue(page, out var list)) throw new InvalidOperationException("No edits on this page.");
        int i = list.FindIndex(e => ReferenceEquals(e, edit));
        if (i < 0) throw new InvalidOperationException("The edit is not in the log.");
        list.RemoveAt(i);
        Snap(page, list);
        return i;
    }

    internal void Insert(PageId page, int index, ContentEdit edit)
    {
        _thread.AssertOnThread();
        if (!_pages.TryGetValue(page, out var list)) _pages[page] = list = [];
        list.Insert(Math.Min(index, list.Count), edit);
        Snap(page, list);
    }

    private void Snap(PageId page, List<ContentEdit> list) =>
        ImmutableInterlocked.AddOrUpdate(ref _snapshot, page, [.. list], (_, _) => [.. list]);
}

/// <summary>한 쪽에 편집 여러 개를 한 번에 덧붙이는 명령. 실행·취소마다 쪽 버전을 올려 타일·썸네일을 다시 그리게 한다.</summary>
public sealed class ContentEditCommand(PageId page, IReadOnlyList<ContentEdit> edits) : IUndoableCommand
{
    private readonly ContentEdit[] _edits = edits.Count == 0 ? throw new ArgumentException("No edits.", nameof(edits)) : [.. edits];

    public PageId Page => page;
    public IReadOnlyList<ContentEdit> Edits => _edits;

    public void Execute(DocumentSession s)
    {
        if (s.IndexOf(page) < 0) throw new InvalidOperationException("The page is not in the document.");
        foreach (var e in _edits) s.Content.Append(page, e);
        s.BumpVersion(page);
    }

    public void Undo(DocumentSession s)
    {
        for (int i = _edits.Length - 1; i >= 0; i--) s.Content.RemoveLast(page, _edits[i]);
        s.BumpVersion(page);
    }
}

/// <summary>
/// 문단 편집 명령(M5 C14). previous를 next로 대체한다(이전 편집이 쌓이지 않음). previous만 있으면 되돌리기(원래 문단으로), next만 있으면 새 편집 덧붙이기.
/// 실행 취소는 이전 편집의 자리까지 정확히 원래 상태로 돌린다.
/// </summary>
public sealed class ParagraphEditCommand(PageId page, ReplaceParagraphEdit? previous, ReplaceParagraphEdit? next) : IUndoableCommand
{
    private int _slot = -1;

    public PageId Page => page;
    public ReplaceParagraphEdit? Previous => previous;
    public ReplaceParagraphEdit? Next => next;

    public void Execute(DocumentSession s)
    {
        if (previous is null && next is null) throw new InvalidOperationException("Nothing to do.");
        if (s.IndexOf(page) < 0) throw new InvalidOperationException("The page is not in the document.");
        if (previous is not null) _slot = s.Content.Remove(page, previous);
        if (next is not null)
        {
            if (previous is null) s.Content.Append(page, next);
            else s.Content.Insert(page, _slot, next);
        }
        s.BumpVersion(page);
    }

    public void Undo(DocumentSession s)
    {
        if (next is not null)
        {
            if (previous is null) s.Content.RemoveLast(page, next);
            else s.Content.Remove(page, next);
        }
        if (previous is not null) s.Content.Insert(page, _slot, previous);
        s.BumpVersion(page);
    }
}
