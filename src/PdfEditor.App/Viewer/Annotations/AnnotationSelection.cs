using PdfEditor.Document;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Viewer.Annotations;

/// <summary>선택된 주석 (한 번에 한 쪽만). UI 스레드에서만 쓴다.</summary>
public sealed class AnnotationSelection
{
    private List<AnnotId> _ids = [];

    public PageId? Page { get; private set; }
    public IReadOnlyList<AnnotId> Ids => _ids;
    public event Action? Changed;

    public void Set(PageId page, IReadOnlyList<AnnotId> ids)
    {
        var next = ids.Distinct().ToList();
        if (Page == page && _ids.SequenceEqual(next)) return;
        if (next.Count == 0 && Page is null) return;
        Page = next.Count == 0 ? null : page;
        _ids = next;
        Changed?.Invoke();
    }

    /// <summary>이미 있으면 빼고, 없으면 더한다. 다른 쪽이면 새로 선택한다.</summary>
    public void Toggle(PageId page, AnnotId id)
    {
        if (Page != page) { Set(page, [id]); return; }
        var next = _ids.ToList();
        if (!next.Remove(id)) next.Add(id);
        Set(page, next);
    }

    public void Clear()
    {
        if (_ids.Count == 0 && Page is null) return;
        Page = null;
        _ids = [];
        Changed?.Invoke();
    }

    /// <summary>없어진 Id를 뺀다 (실행 취소 뒤).</summary>
    public void Prune(PageAnnotations current)
    {
        if (Page != current.Page) return;
        var alive = current.Items.Select(v => v.Data.Id).ToHashSet();
        var next = _ids.Where(alive.Contains).ToList();
        if (next.Count != _ids.Count) Set(current.Page, next);
    }
}
