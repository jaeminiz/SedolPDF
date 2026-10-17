using System.Collections.Immutable;
using System.Security.Cryptography;
using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Commands;
using PdfEditor.Document.Saving;

namespace PdfEditor.Document;

/// <summary>
/// 열린 문서 하나의 상태. PDFium 객체(Document, UndoStash, 페이지 핸들)와 PageId 작업 목록은
/// 전용 스레드에서만 만지고, UI는 Publish()가 교체하는 Pages 스냅숏만 읽는다.
/// </summary>
public sealed class DocumentSession : IAsyncDisposable
{
    private readonly PdfDocument _doc;
    private PdfDocument? _stash;
    private bool _disposed;

    // --- 전용 스레드 전용 작업 상태 ---
    private readonly List<PageId> _order = [];
    private readonly Dictionary<PageId, int> _versions = new();
    private readonly Dictionary<PageId, (int Version, float W, float H)> _sizes = new();
    private readonly PageHandleCache _cache = new();
    private long _nextId = 1;
    private readonly Dictionary<PageId, int> _annotRevisions = new();
    private ImmutableArray<PageInfo> _pages = [];

    private long _savedVersion;
    private volatile bool _originalsEditable = true;

    private DocumentSession(PdfiumThread t, PdfDocument doc, string? filePath, string displayName, string? password, byte[]? sha)
    {
        Thread = t;
        _doc = doc;
        FilePath = filePath;
        DisplayName = displayName;
        Password = password;
        OriginalSha256 = sha;
        Commands = new CommandStack(this);
        Annotations = new AnnotationStore(t, BumpAnnotRevision);
        Permissions = doc.Permissions;
        IsEncrypted = doc.IsEncrypted;
        WasRepaired = !doc.HasValidCrossReferenceTable;
        int n = doc.PageCount;
        for (int i = 0; i < n; i++)
        {
            var id = new PageId(_nextId++);
            _order.Add(id);
            _versions[id] = 0;
        }
        Publish();
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string? FilePath { get; private set; }
    /// <summary>파일 이름, 또는 호출자가 정한 이름(새 문서). 화면 문구는 호출자가 리소스에서 가져와 넣는다.</summary>
    public string DisplayName { get; set; }
    public string? Password { get; }
    /// <summary>디스크에 있는 원본(열 때, 또는 마지막으로 저장한 뒤)의 SHA-256. 복구 때 원본이 바뀌었는지 가린다.</summary>
    public byte[]? OriginalSha256 { get; private set; }
    public PdfiumThread Thread { get; }
    public PdfPermissions Permissions { get; }
    public bool IsEncrypted { get; }
    public bool WasRepaired { get; }
    public CommandStack Commands { get; }
    public AnnotationStore Annotations { get; }

    /// <summary>
    /// 원본 주석을 다 붙였거나(또는 실패를 기록했거나) 끝나면 완료된다. 예외로 끝나지 않는다.
    /// 주석·쪽 명령을 만들기 전에 기다린다 (그 사이 쪽 순서가 바뀌지 않게).
    /// </summary>
    public Task AnnotationsReady { get; private set; } = Task.CompletedTask;

    /// <summary>원본 주석을 읽지 못했으면 false (원본은 표시만, 새 주석은 가능).</summary>
    public bool OriginalAnnotationsEditable => _originalsEditable;

    /// <summary>원본 주석을 읽지 못했음을 기록한다 (가져오기·병합 때 호출).</summary>
    public void MarkOriginalAnnotationsReadOnly() => _originalsEditable = false;

    /// <summary>전용 스레드 전용.</summary>
    public PdfDocument Document
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _doc; }
    }

    /// <summary>실행 취소용 임시 문서. 처음 쓸 때 만든다. 전용 스레드 전용.</summary>
    public PdfDocument UndoStash
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Thread.AssertOnThread();
            return _stash ??= PdfDocument.CreateNew(Thread);
        }
    }

    /// <summary>UI 스레드에서 읽어도 되는 스냅숏.</summary>
    public ImmutableArray<PageInfo> Pages => _pages;

    /// <summary>Publish() 끝에 전용 스레드에서 발생한다.</summary>
    public event Action<PagesChangedEventArgs>? PagesChanged;

    public bool IsDirty => Commands.Version != _savedVersion;

    /// <summary>명령 스택 밖에서 만든 내용(병합 결과 등)을 저장 안 된 상태로 표시한다. 취소로 Version이 돌아와도 계속 변경됨.</summary>
    public void MarkModified() => _savedVersion = -1;

    public void MarkSaved(string path, byte[]? fileSha256 = null)
    {
        if (fileSha256 is not null) OriginalSha256 = fileSha256;
        FilePath = path;
        DisplayName = Path.GetFileName(path);
        _savedVersion = Commands.Version;
    }

    // --- 만들기 ---

    public static async Task<DocumentSession> OpenAsync(PdfiumThread t, string path, string? password, CancellationToken ct = default, bool deferOriginals = false)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var sha = SHA256.HashData(bytes);
        return await t.InvokeAsync(() =>
        {
            var doc = PdfDocument.OpenBytes(t, bytes, password);
            try
            {
                var s = new DocumentSession(t, doc, path, Path.GetFileName(path), password, sha);
                s.BeginLoadingOriginals(bytes, password, deferOriginals);
                return s;
            }
            catch { doc.Dispose(); throw; }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 복구본(RecoveryStore가 쓴 PDF)을 연다. OriginalSha256·DisplayName은 항목에서 가져오고,
    /// bindToOriginal이면 FilePath = 원본 경로, 아니면 제목 없는 문서(FilePath null). 더티 상태로 시작한다.
    /// </summary>
    public static async Task<DocumentSession> OpenRecoveredAsync(PdfiumThread t, Recovery.RecoveryEntry e, bool bindToOriginal,
        string? password, CancellationToken ct = default, bool deferOriginals = false)
    {
        var bytes = await File.ReadAllBytesAsync(e.RecoveryPdfPath, ct).ConfigureAwait(false);
        byte[]? sha = e.OriginalSha256Hex is { Length: > 0 } hex ? Convert.FromHexString(hex) : null;
        string? path = bindToOriginal ? e.OriginalPath : null;
        var s = await t.InvokeAsync(() =>
        {
            var doc = PdfDocument.OpenBytes(t, bytes, password);
            try
            {
                var s = new DocumentSession(t, doc, path, e.DisplayName, password, sha);
                s.BeginLoadingOriginals(bytes, password, deferOriginals);
                return s;
            }
            catch { doc.Dispose(); throw; }
        }, ct).ConfigureAwait(false);
        s.MarkModified();
        return s;
    }

    public static Task<DocumentSession> CreateNewAsync(PdfiumThread t, float widthPt = 595.28f, float heightPt = 841.89f)
        => t.InvokeAsync(() =>
        {
            var doc = PdfDocument.CreateNew(t);
            try
            {
                doc.AddPage(widthPt, heightPt);
                return new DocumentSession(t, doc, null, "Untitled", null, null);
            }
            catch { doc.Dispose(); throw; }
        });

    public static Task<DocumentSession> CreateEmptyAsync(PdfiumThread t)
        => t.InvokeAsync(() =>
        {
            var doc = PdfDocument.CreateNew(t);
            try { return new DocumentSession(t, doc, null, "Untitled", null, null); }
            catch { doc.Dispose(); throw; }
        });

    // --- 원본 주석 불러오기 ---

    private const int CountChunk = 32;
    private const float RectTolerancePt = 0.5f;

    /// <summary>
    /// 전용 스레드에서, 세션을 만든 직후 부른다. 배경에서 ① 쪽마다 주석 수를 세고(0개면 PDFsharp를 열지 않음)
    /// ② PDFsharp로 읽고 ③ 전용 스레드에서 쪽마다 붙인다. 모두 일반 줄만 쓴다.
    /// </summary>
    private void BeginLoadingOriginals(byte[] bytes, string? password, bool defer)
    {
        Thread.AssertOnThread();
        var ids = _order.ToArray();
        if (defer) _originalsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AnnotationsReady = LoadOriginalsAsync(ids, bytes, password);
    }

    // 화면이 문서를 열 때만 쓴다: 첫 화면 타일이 PDFium 스레드를 먼저 쓰도록 주석 개수 세기를 미룬다.
    private TaskCompletionSource? _originalsGate;
    private const int OriginalsGateTimeoutMs = 1500;

    /// <summary>미뤄 둔 원본 주석 불러오기를 시작해도 된다고 알린다(첫 화면 타일을 그린 뒤). 여러 번·어느 스레드에서 불러도 된다. 미루지 않은 세션에서는 아무 일도 없다.</summary>
    public void ReleaseOriginalsLoad() => _originalsGate?.TrySetResult();

    private async Task LoadOriginalsAsync(PageId[] ids, byte[] bytes, string? password)
    {
        try
        {
            // 렌더러가 없거나 타일이 안 그려져도 도구가 영원히 기다리지 않게 시간 제한을 둔다.
            if (_originalsGate is { } gate)
                await Task.WhenAny(gate.Task, Task.Delay(OriginalsGateTimeoutMs)).ConfigureAwait(false);
            var withAnnots = new List<int>();
            for (int from = 0; from < ids.Length; from += CountChunk)
            {
                int start = from;
                var part = await Thread.InvokeAsync(() =>
                {
                    var r = new List<int>();
                    for (int i = start; i < Math.Min(ids.Length, start + CountChunk); i++)
                    {
                        int idx = _order.IndexOf(ids[i]);
                        if (idx < 0) continue;
                        using var page = _doc.LoadPage(idx);
                        if (page.AnnotationCount > 0) r.Add(i);
                    }
                    return r;
                }).ConfigureAwait(false);
                withAnnots.AddRange(part);
            }
            if (withAnnots.Count == 0) return;

            IReadOnlyList<IReadOnlyList<OriginalAnnotation>> read;
            try { read = await Task.Run(() => AnnotationReader.Read(bytes, password, withAnnots)).ConfigureAwait(false); }
            catch (AnnotationReadException) { _originalsEditable = false; return; }

            await Thread.InvokeAsync(() =>
            {
                for (int k = 0; k < withAnnots.Count; k++)
                    AttachOriginalsVerified(ids[withAnnots[k]], read[k]);
                Annotations.Publish();
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 닫히는 중이거나 PDFium 오류: 원본 주석은 표시만 한다. AnnotationsReady는 예외로 끝나지 않는다.
            _originalsEditable = false;
        }
    }

    /// <summary>
    /// 전용 스레드 전용. PDFium의 같은 번호 주석과 Subtype·Rect(차이 ≤ 0.5pt)가 다르면 그 항목은 붙이지 않는다(읽기 전용).
    /// 쪽이 문서에 없으면 아무것도 하지 않는다. Publish는 호출자 몫.
    /// </summary>
    public void AttachOriginalsVerified(PageId id, IReadOnlyList<OriginalAnnotation> originals)
    {
        Thread.AssertOnThread();
        int idx = _order.IndexOf(id);
        if (idx < 0) return;
        using var page = _doc.LoadPage(idx);
        int count = page.AnnotationCount;
        var verified = new List<OriginalAnnotation>(originals.Count);
        foreach (var o in originals)
            verified.Add(o.Data is null || Matches(page, count, o) ? o : o with { Data = null, Reason = ReadOnlyReason.Malformed });
        if (!SavePolicy.CanPatch(this)) MakePatchDependentReadOnly(verified);
        Annotations.AttachOriginals(id, verified);
    }

    /// <summary>
    /// 패치를 못 하는(암호) 문서: 저장 때 PDFsharp 패치가 있어야 쓸 수 있는 원본(선·폴리라인·다각형·FreeText·Caret 묶음과 그 부모,
    /// 클라우드 Square)은 모델에 넣지 않는다 — 고쳐도 저장 때 조용히 버려지기 때문이다. PDFium이 원본 모양 그대로 그린다.
    /// </summary>
    private static void MakePatchDependentReadOnly(List<OriginalAnnotation> list)
    {
        var parents = new HashSet<int>(list.Where(o => o.GroupParentIndex is not null && o.Data is null or { Kind: AnnotKind.Caret }).Select(o => o.GroupParentIndex!.Value));
        for (int i = 0; i < list.Count; i++)
        {
            var o = list[i];
            if (o.Data is not { } d) continue;
            if (d.Kind.IsPatchKind() || d.CloudIntensity > 0f || parents.Contains(o.Index))
                list[i] = o with { Data = null, Reason = ReadOnlyReason.OriginalAppearanceOnly };
        }
    }

    /// <summary>패치를 못 하는 문서에 패치가 필요한 주석이 들어 있으면 저장·인쇄·복구를 막는다 (조용히 버리지 않는다).</summary>
    internal void EnsureNoPatchDependentEntries()
    {
        if (SavePolicy.CanPatch(this)) return;
        if (Annotations.HasLivePatchDependentEntries())
            throw new InvalidOperationException(
                "The document cannot be patched (encrypted), but it contains annotations that need the patch stage (line, polygon, free text, caret, stamp, attachment or cloud). Refusing to write them silently dropped.");
    }

    private static bool Matches(PdfPage page, int count, OriginalAnnotation o)
    {
        if (o.Index < 0 || o.Index >= count) return false;
        try
        {
            using var a = page.GetAnnotation(o.Index);
            if (a.Subtype != o.Subtype) return false;
            var r = a.Rect;
            var d = o.Data!.Rect;
            return Math.Abs(r.Left - d.Left) <= RectTolerancePt && Math.Abs(r.Bottom - d.Bottom) <= RectTolerancePt
                && Math.Abs(r.Right - d.Right) <= RectTolerancePt && Math.Abs(r.Top - d.Top) <= RectTolerancePt;
        }
        catch (PdfiumException) { return false; }
    }

    // --- 명령용 변경 메서드 (전용 스레드 전용) ---

    public int IndexOf(PageId id)
    {
        Thread.AssertOnThread();
        return _order.IndexOf(id);
    }

    public IReadOnlyList<PageId> NewPageIds(int count)
    {
        Thread.AssertOnThread();
        var ids = new PageId[count];
        for (int i = 0; i < count; i++) ids[i] = new PageId(_nextId++);
        return ids;
    }

    /// <summary>
    /// 쪽 번호를 끼운다. 한 번 쓴 PageId(실행 취소로 되살리기·다시 실행)는 지난 버전 다음 번호를 받는다:
    /// (PageId, Version)이 다른 모양을 가리키면 늦게 도착한 옛 렌더 결과가 통과되기 때문이다(Review Focus 1).
    /// </summary>
    public void InsertPageIds(int at, IReadOnlyList<PageId> ids)
    {
        Thread.AssertOnThread();
        _order.InsertRange(at, ids);
        foreach (var id in ids) _versions[id] = _versions.TryGetValue(id, out var v) ? v + 1 : 0;
    }

    /// <summary>쪽 번호를 뺀다. 버전 기록은 남겨 두어 같은 PageId가 돌아오면 이어서 번호를 매긴다.</summary>
    public void RemovePageIdAt(int index)
    {
        Thread.AssertOnThread();
        var id = _order[index];
        _cache.Evict(id);
        _order.RemoveAt(index);
        _sizes.Remove(id);
    }

    /// <summary>PDFium MovePages와 같은 의미: 옮긴 쪽들이 주어진 순서로 연속해서 놓이고 첫 쪽의 결과 인덱스가 destIndex.</summary>
    public void MovePageIds(IReadOnlyList<PageId> ids, int destIndex)
    {
        Thread.AssertOnThread();
        var set = new HashSet<PageId>(ids);
        _order.RemoveAll(set.Contains);
        _order.InsertRange(destIndex, ids);
    }

    public void BumpVersion(PageId id)
    {
        Thread.AssertOnThread();
        _versions[id] = _versions[id] + 1;
        _cache.Evict(id);
    }

    /// <summary>
    /// 타일에서 숨길 주석 번호 집합이 바뀌었음을 알린다 (쪽 모양 Version과 별개). 페이지 핸들 캐시는 비우지 않는다.
    /// </summary>
    internal void BumpAnnotRevision(PageId id)
    {
        Thread.AssertOnThread();
        _annotRevisions[id] = _annotRevisions.GetValueOrDefault(id) + 1;
    }

    /// <summary>작업 목록으로 새 Pages 스냅숏을 만들어 교체하고 PagesChanged를 일으킨다.</summary>
    public void Publish()
    {
        Thread.AssertOnThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var old = _pages;
        var oldById = new Dictionary<PageId, PageInfo>(old.Length);
        foreach (var p in old) oldById[p.Id] = p;

        var b = ImmutableArray.CreateBuilder<PageInfo>(_order.Count);
        var changed = new List<PageId>();
        for (int i = 0; i < _order.Count; i++)
        {
            var id = _order[i];
            int ver = _versions[id];
            if (!_sizes.TryGetValue(id, out var sz) || sz.Version != ver)
            {
                var s = _doc.GetPageSizePt(i);
                sz = (ver, s.Width, s.Height);
                _sizes[id] = sz;
            }
            var info = new PageInfo(id, ver, sz.W, sz.H, _annotRevisions.GetValueOrDefault(id));
            b.Add(info);
            if (!oldById.TryGetValue(id, out var prev) || prev != info) changed.Add(id);
        }
        var next = b.MoveToImmutable();
        bool structure = old.Length != next.Length;
        for (int i = 0; !structure && i < next.Length; i++)
            structure = old[i].Id != next[i].Id;

        ImmutableInterlocked.InterlockedExchange(ref _pages, next);
        if (changed.Count > 0 || structure)
            PagesChanged?.Invoke(new PagesChangedEventArgs(changed, structure));
        Annotations.Publish();
    }

    // --- 페이지 핸들 재사용 (전용 스레드 전용) ---

    public PdfPage GetLoadedPage(PageId id)
    {
        Thread.AssertOnThread();
        return _cache.GetPage(id, () => LoadPageOf(id));
    }

    public PdfTextPage GetLoadedText(PageId id)
    {
        Thread.AssertOnThread();
        return _cache.GetText(id, () => LoadPageOf(id));
    }

    private PdfPage LoadPageOf(PageId id)
    {
        int idx = _order.IndexOf(id);
        if (idx < 0) throw new ArgumentException("문서에 없는 페이지입니다.", nameof(id));
        return _doc.LoadPage(idx);
    }

    /// <summary>
    /// 주석 모델의 바뀐 내용을 PDFium 문서에 반영한다 (저장·인쇄·자동 복구 직전). 전용 스레드 전용.
    /// 바뀐 것이 없으면 아무것도 하지 않는다. 문서에 없는(삭제되어 UndoStash에 있는) 쪽은 다음 Flush로 미룬다.
    /// </summary>
    public void FlushAnnotations(IAppearanceSource appearance)
    {
        Thread.AssertOnThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        AnnotationFlusher.Flush(this, appearance);
    }

    /// <summary>
    /// 저장·복구용: appearance가 없는데 반영할 주석 변경이 있으면 InvalidOperationException, 있으면 Flush한다.
    /// 예외가 나면 문서가 일부만 바뀌었을 수 있으므로 호출자는 쓰기를 중단해야 한다.
    /// </summary>
    public PatchPlan FlushIfNeeded(IAppearanceSource? appearance)
    {
        Thread.AssertOnThread();
        // 저장·인쇄·자동 복구가 모두 지나는 곳: 패치를 못 하는 문서에서 패치 길 주석을 조용히 버리지 않는다.
        EnsureNoPatchDependentEntries();
        if (appearance is null)
        {
            if (Annotations.HasUnflushedChanges)
                throw new InvalidOperationException("Annotation changes need an IAppearanceSource to be written.");
        }
        else
        {
            FlushAnnotations(appearance);
        }
        CurrentAppearance = appearance;
        return PlanFactory(this);
    }

    /// <summary>
    /// 저장 파일에 적용할 패치 계획을 모델에서 만드는 함수(기본: PatchPlanBuilder.Build).
    /// 돌려준 계획은 임시 파일(SaveCopy 결과)에만 적용한다 — 편집 중인 문서는 바꾸지 않는다.
    /// 테스트가 손으로 만든 계획을 끼울 수 있도록 internal.
    /// </summary>
    internal Func<DocumentSession, PatchPlan> PlanFactory { get; set; } = static s => PatchPlanBuilder.Build(s, s.CurrentAppearance);

    /// <summary>가장 최근 FlushIfNeeded에 쓴 AP 공급자. 기본 PlanFactory가 FreeText의 AP를 만들 때 쓴다.</summary>
    internal IAppearanceSource? CurrentAppearance { get; private set; }

    /// <summary>구조를 바꾸는 명령 전후에 CommandStack이 부른다.</summary>
    public void ReleasePageHandles()
    {
        Thread.AssertOnThread();
        _cache.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await Thread.InvokeAsync(() =>
            {
                // 명령이 가진 문서(ImportPagesCommand의 원본 등)도 전용 스레드에서 닫는다 — 부른 스레드(UI)를 막지 않는다.
                Commands.Clear();
                _cache.Clear();
                _stash?.Dispose();
                _doc.Dispose();
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }   // PDFium 스레드가 먼저 닫힌 경우
        catch (TaskCanceledException) { }
    }
}
