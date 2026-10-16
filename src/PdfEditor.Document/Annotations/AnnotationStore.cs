using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations.Reading;

namespace PdfEditor.Document.Annotations;

/// <summary>
/// 저장 때 쓰는 항목(Flush 전용). PdfIndex: PDFium 주석 번호(아직 PDF에 없으면 null).
/// Owned: 우리 그리기 층이 그린다(새 주석, 손댄 원본). 원본은 한 번 Owned가 되면 되돌아가지 않는다.
/// </summary>
internal sealed class AnnotEntry
{
    public Annotation Current = null!;
    public int? PdfIndex;
    public bool IsOriginal;
    public bool Owned;
    public bool Deleted;
    public long Revision;
    public long FlushedRevision;

    /// <summary>PDF에 반영할 것이 남았는가. PDF에 없던 채 지워진 항목은 할 일이 없다.</summary>
    public bool NeedsFlush => Revision != FlushedRevision && !(Deleted && PdfIndex is null);
}

/// <summary>
/// 문서의 주석 모델. 변경(Apply 등)은 PDFium 전용 스레드에서만 하고, UI는 Publish()가 교체하는
/// 스냅숏(Get)만 읽는다.
/// </summary>
public sealed class AnnotationStore
{
    private sealed class PageState
    {
        public readonly List<AnnotEntry> Entries = [];
        public bool Attached;
        public bool Dirty;
    }

    private readonly PdfiumThread _thread;
    private readonly Action<PageId> _bumpAnnotRevision;
    private readonly Dictionary<PageId, PageState> _pages = new();
    private ImmutableDictionary<PageId, PageAnnotations> _snapshot = ImmutableDictionary<PageId, PageAnnotations>.Empty;
    private long _nextId;

    internal AnnotationStore(PdfiumThread thread, Action<PageId> bumpAnnotRevision)
    {
        _thread = thread;
        _bumpAnnotRevision = bumpAnnotRevision;
    }

    /// <summary>전용 스레드에서 Publish() 끝에 발생한다 (한 번에 바뀐 쪽 모두).</summary>
    public event Action<AnnotationsChangedEventArgs>? Changed;

    // --- UI에서 호출 가능 ---

    public AnnotId NewId() => new(Interlocked.Increment(ref _nextId));

    public PageAnnotations Get(PageId page)
        => _snapshot.TryGetValue(page, out var p) ? p : PageAnnotations.Empty(page);

    // --- 전용 스레드 전용 ---

    public void AttachOriginals(PageId page, IReadOnlyList<OriginalAnnotation> originals)
    {
        _thread.AssertOnThread();
        var st = State(page);
        if (st.Attached) throw new InvalidOperationException("Originals are already attached to this page.");
        st.Attached = true;
        foreach (var o in originals)
        {
            if (o.Data is null) continue;
            st.Entries.Add(new AnnotEntry
            {
                Current = o.Data with { Id = NewId() },
                PdfIndex = o.Index,
                IsOriginal = true,
            });
        }
        st.Dirty = true;
    }

    public void Apply(PageId page, Annotation? before, Annotation? after)
    {
        _thread.AssertOnThread();
        if (before is null && after is null) throw new ArgumentException("before and after are both null.");
        if (before is not null && after is not null && before.Id != after.Id)
            throw new ArgumentException("Replaced annotation must keep its Id.");

        var st = State(page);
        var id = (before ?? after)!.Id;
        var entry = st.Entries.Find(e => e.Current.Id == id);

        if (before is null)
        {
            if (entry is null)
            {
                st.Entries.Add(new AnnotEntry { Current = after!, Owned = true, Revision = 1 });
            }
            else if (entry.Deleted)
            {
                entry.Deleted = false;
                entry.Current = after!;
                entry.Revision++;
            }
            else throw new InvalidOperationException("An annotation with the same Id already exists.");
        }
        else
        {
            if (entry is null || entry.Deleted || !ReferenceEquals(entry.Current, before))
                throw new InvalidOperationException("Annotation changed or missing (command built from stale state).");
            bool wasHidden = entry.IsOriginal && entry.Owned;
            if (entry.IsOriginal) entry.Owned = true;
            if (after is null) entry.Deleted = true; else entry.Current = after;
            entry.Revision++;
            if (entry.IsOriginal && !wasHidden) _bumpAnnotRevision(page);
        }
        st.Dirty = true;
    }

    /// <summary>타일에서 숨길 PDFium 주석 번호: 손대거나 지운 원본.</summary>
    public IReadOnlySet<int> HiddenPdfIndices(PageId page)
    {
        _thread.AssertOnThread();
        var set = new HashSet<int>();
        if (_pages.TryGetValue(page, out var st))
            foreach (var e in st.Entries)
                if (e.IsOriginal && e.Owned && e.PdfIndex is int i) set.Add(i);
        return set;
    }

    /// <summary>쪽을 회전한 뒤: 손댄 Text 주석의 아이콘을 다시 그리도록 Revision을 올린다.</summary>
    public void MarkAppearanceStale(PageId page)
    {
        _thread.AssertOnThread();
        if (!_pages.TryGetValue(page, out var st)) return;
        foreach (var e in st.Entries)
            if (e.IsOriginal && e.Owned && !e.Deleted && e.Current.Kind == AnnotKind.Text) e.Revision++;
    }

    public bool HasUnflushedChanges
    {
        get
        {
            _thread.AssertOnThread();
            return _pages.Values.Any(p => p.Entries.Any(e => e.NeedsFlush));
        }
    }

    public void Publish()
    {
        _thread.AssertOnThread();
        List<PageId>? changed = null;
        foreach (var (id, st) in _pages)
        {
            if (!st.Dirty) continue;
            st.Dirty = false;
            var items = ImmutableArray.CreateBuilder<AnnotationView>(st.Entries.Count);
            foreach (var e in st.Entries)
                if (!e.Deleted) items.Add(new AnnotationView(e.Current, !e.IsOriginal || e.Owned));
            ImmutableInterlocked.AddOrUpdate(ref _snapshot, id,
                new PageAnnotations(id, items.ToImmutable()), (_, _) => new PageAnnotations(id, items.ToImmutable()));
            (changed ??= []).Add(id);
        }
        if (changed is not null) Changed?.Invoke(new AnnotationsChangedEventArgs(changed));
    }

    // --- Flush용 (Task 7) ---

    internal IEnumerable<(PageId Page, IReadOnlyList<AnnotEntry> Entries)> PagesWithChanges()
    {
        _thread.AssertOnThread();
        foreach (var (id, st) in _pages)
            if (st.Entries.Any(e => e.NeedsFlush)) yield return (id, st.Entries.ToArray());
    }

    /// <summary>Flush가 끝난 뒤: 새 PDFium 번호를 받고, 지워진 항목은 버리고, 반영한 Revision을 기록한다.</summary>
    internal void CompleteFlush(PageId page, IReadOnlyDictionary<AnnotEntry, int> newIndices)
    {
        _thread.AssertOnThread();
        var st = State(page);
        var hiddenBefore = HiddenPdfIndices(page);
        st.Entries.RemoveAll(e => e.Deleted);
        foreach (var e in st.Entries)
        {
            if (newIndices.TryGetValue(e, out var idx)) e.PdfIndex = idx;
            e.FlushedRevision = e.Revision;
        }
        if (!hiddenBefore.SetEquals(HiddenPdfIndices(page))) _bumpAnnotRevision(page);
    }

    private PageState State(PageId page)
    {
        if (!_pages.TryGetValue(page, out var st)) _pages[page] = st = new PageState();
        return st;
    }
}
