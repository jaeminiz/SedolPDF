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
    /// <summary>손대지 않은 원본을 지웠다(패치 가능 문서: PDFium에는 그대로 남음). 실행 취소하면 손대지 않은 원본으로 돌아간다.</summary>
    public bool UntouchedDelete;
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
        public ImmutableArray<ReadOnlyAnnotationInfo> ReadOnly = [];
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
        // 묶음 Caret의 부모는 같은 쪽 원본이어야 한다(PDFium 번호로 찾는다). 부모를 붙이지 못했으면 그 Caret도 붙이지 않는다(읽기 전용).
        // 답글·상태도 같다: 부모가 이 쪽에서 편집 가능하게 붙은 경우에만 붙인다(부모 쪽 사슬이 하나라도 끊기면 붙이지 않는다).
        var byIndex = new Dictionary<int, OriginalAnnotation>();
        foreach (var o in originals) byIndex[o.Index] = o;
        var memo = new Dictionary<int, bool>();
        bool Attachable(int index, HashSet<int> visiting)
        {
            if (memo.TryGetValue(index, out var known)) return known;
            if (!byIndex.TryGetValue(index, out var o) || o.Data is null || !visiting.Add(index)) return false;
            bool ok = (o.GroupParentIndex is int g ? byIndex.TryGetValue(g, out var gp) && gp.Data is { Kind: AnnotKind.StrikeOut } : true)
                && (o.ReplyParentIndex is int r ? Attachable(r, visiting) : true);
            visiting.Remove(index);
            return memo[index] = ok;
        }
        var parentIndices = new HashSet<int>(originals.Where(o => o.ReplyParentIndex is not null).Select(o => o.ReplyParentIndex!.Value));
        var idByPdfIndex = new Dictionary<int, AnnotId>();
        foreach (var o in originals)
            if (o.Data is not null && (o.Data.Kind == AnnotKind.StrikeOut || parentIndices.Contains(o.Index))) idByPdfIndex[o.Index] = NewId();
        var readOnly = ImmutableArray.CreateBuilder<ReadOnlyAnnotationInfo>();
        foreach (var o in originals)
        {
            if (o.Data is null)
            {
                if (o.Info is { } ro) readOnly.Add(ro);   // 읽기 전용 원본(Popup은 Info가 없다)
                continue;
            }
            // 편집 가능으로 읽었지만 붙이지 못한 항목(부모 사슬 끊김)은 읽기 전용 답글로 목록에 남긴다.
            void Skip() { if (ReadOnlyAnnotationInfo.From(o, ReadOnlyReason.Reply) is { } ro) readOnly.Add(ro); }
            if (!Attachable(o.Index, [])) { Skip(); continue; }
            var data = o.Data;
            if (o.GroupParentIndex is int pi)
            {
                if (!idByPdfIndex.TryGetValue(pi, out var parentId)) { Skip(); continue; }
                data = data with { InReplyTo = parentId, ReplyKind = ReplyType.Group };
            }
            else if (o.ReplyParentIndex is int ri)
            {
                if (!idByPdfIndex.TryGetValue(ri, out var parentId)) { Skip(); continue; }
                data = data with { InReplyTo = parentId, ReplyKind = ReplyType.Reply };
            }
            st.Entries.Add(new AnnotEntry
            {
                Current = data with { Id = idByPdfIndex.TryGetValue(o.Index, out var preset) ? preset : NewId() },
                PdfIndex = o.Index,
                IsOriginal = true,
            });
        }
        st.ReadOnly = readOnly.ToImmutable();
        st.Dirty = true;
    }

    /// <summary>
    /// 텍스트 바꾸기 묶음(StrikeOut + Caret, A11): 대표(StrikeOut)가 맨 앞, 이어서 그것을 Group으로 가리키는 항목들.
    /// 묶이지 않은 주석은 자기 하나만, 없는 주석은 빈 목록. 스냅숏(Publish된 것)을 읽으므로 어느 스레드에서나 부를 수 있다.
    /// </summary>
    public IReadOnlyList<Annotation> GroupOf(PageId page, AnnotId id) => Get(page).GroupOf(id);

    /// <summary>뿌리 + 답글(시각순) + 상태(시각순). 스냅숏을 읽으므로 어느 스레드에서나 부를 수 있다.</summary>
    public IReadOnlyList<Annotation> ThreadOf(PageId page, AnnotId root) => Get(page).ThreadOf(root);

    /// <summary>그 주석에 직접 단 가장 최근 상태(없으면 None).</summary>
    public ReviewState CurrentState(PageId page, AnnotId root) => Get(page).CurrentState(root);

    /// <summary>묶음 구성원과 각각의 답글·상태 전부(부모를 지울 때 함께 지울 목록).</summary>
    public IReadOnlyList<Annotation> DeleteSetOf(PageId page, AnnotId id) => Get(page).DeleteSetOf(id);

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
                // 손대지 않은 원본을 지웠다가 그대로 되살리면 다시 PDFium이 그리는 원본이다(키·Popup·답글 그대로).
                bool untouched = entry.UntouchedDelete && Equals(entry.Current, after);
                entry.Deleted = false;
                entry.Current = after!;
                entry.Revision++;
                if (untouched)
                {
                    entry.Owned = false;
                    entry.UntouchedDelete = false;
                    _bumpAnnotRevision(page);
                }
            }
            else throw new InvalidOperationException("An annotation with the same Id already exists.");
        }
        else
        {
            if (entry is null || entry.Deleted || !ReferenceEquals(entry.Current, before))
                throw new InvalidOperationException("Annotation changed or missing (command built from stale state).");
            bool wasHidden = entry.IsOriginal && entry.Owned;
            if (after is null && entry.IsOriginal && !entry.Owned) entry.UntouchedDelete = true;
            if (entry.IsOriginal) entry.Owned = true;
            if (after is null) entry.Deleted = true; else entry.Current = after;
            entry.Revision++;
            if (entry.IsOriginal && !wasHidden) _bumpAnnotRevision(page);
        }
        st.Dirty = true;
    }

    /// <summary>
    /// 타일에서 숨길 PDFium 주석 번호: 주석 층이 그리는 항목(손댄 원본, 저장으로 PDF에 들어간 새 주석) 전부와
    /// 지웠지만 아직 Flush 전이라 PDF에 남은 항목. 새 주석을 빼면 저장 뒤 두 번 그려지고(반투명은 진해짐),
    /// 저장 뒤 지운 새 주석이 다시 그린 타일에 남는다(M2 마무리 검토).
    /// </summary>
    public IReadOnlySet<int> HiddenPdfIndices(PageId page)
    {
        _thread.AssertOnThread();
        var set = new HashSet<int>();
        if (_pages.TryGetValue(page, out var st))
            foreach (var e in st.Entries)
                if ((!e.IsOriginal || e.Owned) && e.PdfIndex is int i) set.Add(i);
        return set;
    }

    /// <summary>쪽을 회전한 뒤: 우리가 그리는(새로 만들었거나 손댄) Text 주석의 아이콘을 다시 그리도록 Revision을 올린다.</summary>
    public void MarkAppearanceStale(PageId page)
    {
        _thread.AssertOnThread();
        if (!_pages.TryGetValue(page, out var st)) return;
        foreach (var e in st.Entries)
            if (e.Owned && !e.Deleted && e.Current.Kind == AnnotKind.Text) e.Revision++;
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
            var pageSnapshot = new PageAnnotations(id, items.ToImmutable()) { ReadOnly = st.ReadOnly };
            ImmutableInterlocked.AddOrUpdate(ref _snapshot, id, pageSnapshot, (_, _) => pageSnapshot);
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

    /// <summary>PatchPlanBuilder용: 쪽별 항목 복사본.</summary>
    internal IEnumerable<(PageId Page, IReadOnlyList<AnnotEntry> Entries)> AllPages()
    {
        _thread.AssertOnThread();
        foreach (var (id, st) in _pages) yield return (id, st.Entries.ToArray());
    }

    /// <summary>저장 때 패치가 있어야 파일에 쓸 수 있는 살아 있는 항목(패치 길 유형, 클라우드)이 있는가.</summary>
    internal bool HasLivePatchDependentEntries()
    {
        _thread.AssertOnThread();
        return _pages.Values.Any(st => st.Entries.Any(e => !e.Deleted && (e.Current.NeedsPatch() || e.Current.CloudIntensity > 0f)));
    }

    /// <summary>Flush가 끝난 뒤: 새 PDFium 번호를 받고, 지워진 항목은 버리고, 반영한 Revision을 기록한다.</summary>
    internal void CompleteFlush(PageId page, IReadOnlyDictionary<AnnotEntry, int> newIndices)
    {
        _thread.AssertOnThread();
        var st = State(page);
        // 지워진 항목이 뒤 번호를 당기므로, 살아 남은 항목(손대지 않은 원본 포함)의 새 번호가 모두 있어야 한다.
        // 하나라도 빠지면 이후 Flush가 엉뚱한 주석을 고치므로 아무것도 바꾸기 전에 막는다.
        foreach (var e in st.Entries)
            if (!e.Deleted && !newIndices.ContainsKey(e))
                throw new InvalidOperationException("CompleteFlush needs a new PDF index for every surviving annotation entry.");
        var hiddenBefore = HiddenPdfIndices(page);
        // 패치 가능 문서에서 지운 원본은 PDFium에 남아 있으므로(newIndices에 번호가 있음) 항목도 남긴다.
        st.Entries.RemoveAll(e => e.Deleted && !newIndices.ContainsKey(e));
        foreach (var e in st.Entries)
        {
            e.PdfIndex = newIndices[e] < 0 ? null : newIndices[e];   // -1: PDFium 문서에 없는 패치 길 주석
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
