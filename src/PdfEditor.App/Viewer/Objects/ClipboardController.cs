using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;

namespace PdfEditor.App.Viewer.Objects;

/// <summary>
/// 클립보드에 담은 개체·주석(0.9.0). 문서(세션)마다 따로 기억한다: 다른 문서에서 붙이려 하면 알림만 보여 준다(설계서 2절).
/// PasteCount = 이 클립보드를 같은 쪽에 붙인 횟수(붙일 때마다 10pt씩 더 비킨다).
/// </summary>
public sealed record ClipboardEntry(DocumentSession Session, PageId FromPage, int FromRotation,
    ImmutableArray<ClipItem> Objects, ImmutableArray<Annotation> Annotations)
{
    public int PasteCount { get; set; }
}

/// <summary>
/// 개체·주석 복사/잘라내기/붙여넣기/복제의 흐름(Avalonia 없음, UI 스레드에서 부른다). 앱 전체에 하나(<see cref="Shared"/>) — 여러 창이 같이 쓴다.
/// 권한: 개체는 CanModify와 CanCopy, 주석은 CanAnnotate. 복사 자체는 문서를 바꾸지 않고, 잘라내기·붙여넣기·복제는 명령 하나(개체와 주석이 섞이면 둘)로 실행 취소한다.
/// </summary>
public sealed class ClipboardController
{
    public static ClipboardController Shared { get; } = new();

    /// <summary>마지막으로 복사/잘라낸 것. 복제는 이것을 바꾸지 않는다.</summary>
    public ClipboardEntry? Current { get; private set; }

    /// <summary>클립보드를 비운다(그 문서를 닫을 때, 시험).</summary>
    public void Clear() => Current = null;

    /// <summary>시험용으로 바꿀 수 있는 현재 시각·작성자 제공자.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;
    public Func<string?> Author { get; init; } = () => DocumentTabViewModel.CurrentAuthor;

    public static bool HasSelection(DocumentTabViewModel tab) => tab.Objects.All.Count > 0 || tab.Selection.Ids.Count > 0;

    /// <summary>이 문서에 붙일 수 있는 클립보드가 있나(메뉴의 붙여넣기 사용 가능 여부).</summary>
    public bool CanPaste(DocumentTabViewModel tab) => Current is { } c && ReferenceEquals(c.Session, tab.Session);

    /// <summary>
    /// 고른 개체·주석을 클립보드에 담는다. cut이면 담은 것을 지운다(클립보드는 지우기가 성공했을 때만 바꾼다).
    /// 고른 것이 없으면 false(→ 글자 복사로 넘어간다). 그 밖에는 알림을 띄웠더라도 true(처리함).
    /// </summary>
    public async Task<bool> CopyAsync(DocumentTabViewModel tab, bool cut)
    {
        if (tab.Session is not { } s || !HasSelection(tab)) return false;
        var objs = tab.Objects.All.ToList();
        var annotIds = tab.Selection.Ids.ToList();
        var annotPage = tab.Selection.Page;

        bool objectsAllowed = tab.CanEditContent && s.Permissions.CanCopy;
        if (objs.Count > 0 && !objectsAllowed) { tab.RaiseNotice(Strings.Msg_ObjCopy_NoPermission); objs.Clear(); }
        if (annotIds.Count > 0 && !tab.CanAnnotate) { tab.RaiseNotice(Strings.Msg_AnnotationsRestricted); annotIds.Clear(); }
        if (objs.Count == 0 && annotIds.Count == 0) return true;

        var built = await BuildAsync(s, objs, annotPage, annotIds);
        if (built.Objects.IsDefaultOrEmpty && built.Annotations.IsDefaultOrEmpty)
        {
            if (built.Skipped > 0) tab.RaiseNotice(Strings.Msg_ObjCopy_Partial);
            return true;
        }
        if (cut)
        {
            try
            {
                if (!built.CopiedRefs.IsDefaultOrEmpty && objs.Count > 0)
                    await tab.RunContentCommandAsync(new ContentEditCommand(objs[0].Page,
                        [.. built.CopiedRefs.Select(r => (ContentEdit)new DeactivateEdit(s.Content.NewId(), r))]));
                if (!built.Annotations.IsDefaultOrEmpty && annotPage is { } ap)
                {
                    var page = s.Annotations.Get(ap);
                    var gone = built.Annotations.SelectMany(a => page.DeleteSetOf(a.Id)).DistinctBy(a => a.Id).ToList();
                    if (gone.Count > 0) await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Delete(ap, gone));
                }
            }
            catch (Exception) { return true; }   // 쪽이 그사이 지워짐 등: 아무것도 바꾸지 않고 클립보드도 그대로
            tab.Objects.Clear();
            tab.Selection.Clear();
        }
        Current = new ClipboardEntry(s, built.FromPage, built.FromRotation, built.Objects, built.Annotations);
        if (built.Skipped > 0) tab.RaiseNotice(Strings.Msg_ObjCopy_Partial);
        return true;
    }

    /// <summary>클립보드(이 문서의 것)를 target 쪽에 붙인다. targetRotation = 그 쪽의 회전(0~3).</summary>
    public async Task PasteAsync(DocumentTabViewModel tab, PageId target, int targetRotation)
    {
        if (Current is not { } entry) return;
        if (tab.Session is not { } s) return;
        if (!ReferenceEquals(entry.Session, s)) { tab.RaiseNotice(Strings.Msg_ObjCopy_OtherDocument); return; }
        bool samePage = target == entry.FromPage;
        int k = samePage ? entry.PasteCount + 1 : 1;
        if (await PasteEntryAsync(tab, entry, target, targetRotation, samePage, k) && samePage) entry.PasteCount++;
    }

    /// <summary>고른 개체·주석을 같은 쪽에 하나 더 만든다(복사 + 바로 붙여넣기). 클립보드는 건드리지 않는다.</summary>
    public async Task DuplicateAsync(DocumentTabViewModel tab, int rotation)
    {
        if (tab.Session is not { } s || !HasSelection(tab)) return;
        var objs = tab.Objects.All.ToList();
        var annotIds = tab.Selection.Ids.ToList();
        var annotPage = tab.Selection.Page;
        bool objectsAllowed = tab.CanEditContent && s.Permissions.CanCopy;
        if (objs.Count > 0 && !objectsAllowed) { tab.RaiseNotice(Strings.Msg_ObjCopy_NoPermission); objs.Clear(); }
        if (annotIds.Count > 0 && !tab.CanAnnotate) { tab.RaiseNotice(Strings.Msg_AnnotationsRestricted); annotIds.Clear(); }
        if (objs.Count == 0 && annotIds.Count == 0) return;
        var built = await BuildAsync(s, objs, annotPage, annotIds);
        if (built.Objects.IsDefaultOrEmpty && built.Annotations.IsDefaultOrEmpty)
        {
            if (built.Skipped > 0) tab.RaiseNotice(Strings.Msg_ObjCopy_Partial);
            return;
        }
        var entry = new ClipboardEntry(s, built.FromPage, built.FromRotation, built.Objects, built.Annotations);
        await PasteEntryAsync(tab, entry, built.FromPage, rotation, samePage: true, k: 1);
        if (built.Skipped > 0) tab.RaiseNotice(Strings.Msg_ObjCopy_Partial);
    }

    // ---- 내부 ----

    private sealed record Built(PageId FromPage, int FromRotation, ImmutableArray<ClipItem> Objects, ImmutableArray<ObjectRef> CopiedRefs,
        ImmutableArray<Annotation> Annotations, int Skipped);

    private static async Task<Built> BuildAsync(DocumentSession s, IReadOnlyList<SelectedObject> objs, PageId? annotPage, IReadOnlyList<AnnotId> annotIds)
    {
        return await s.Thread.InvokeAsync(() =>
        {
            ImmutableArray<ClipItem> items = [];
            ImmutableArray<ObjectRef> refs = [];
            int skipped = 0;
            PageId from = default;
            if (objs.Count > 0)
            {
                from = objs[0].Page;
                if (s.IndexOf(from) >= 0)
                {
                    var r = ObjectCopyBuilder.Build(s, from, [.. objs.Select(o => o.Ref)]);
                    items = r.Items; refs = r.CopiedRefs.IsDefault ? [] : r.CopiedRefs; skipped += r.Skipped;
                }
                else skipped += objs.Count;
            }
            ImmutableArray<Annotation> annots = [];
            if (annotIds.Count > 0 && annotPage is { } ap && s.IndexOf(ap) >= 0)
            {
                var a = AnnotationCopyBuilder.Build(s.Annotations.Get(ap), annotIds);
                annots = a.Items; skipped += a.Skipped;
                if (from == default) from = ap;
            }
            int rot = from != default && s.IndexOf(from) >= 0 ? s.GetLoadedPage(from).Rotation : 0;
            return new Built(from, rot, items, refs, annots, skipped);
        });
    }

    /// <summary>true = 붙였다(아무것도 안 바뀌었으면 false).</summary>
    private async Task<bool> PasteEntryAsync(DocumentTabViewModel tab, ClipboardEntry entry, PageId target, int targetRotation, bool samePage, int k)
    {
        var s = entry.Session;
        bool objectsAllowed = tab.CanEditContent && s.Permissions.CanCopy;
        bool annotsAllowed = tab.CanAnnotate;
        var objects = entry.Objects;
        var annots = entry.Annotations;
        if (!objects.IsDefaultOrEmpty && !objectsAllowed) { tab.RaiseNotice(Strings.Msg_ObjCopy_NoPermission); objects = []; }
        if (!annots.IsDefaultOrEmpty && !annotsAllowed) { tab.RaiseNotice(Strings.Msg_AnnotationsRestricted); annots = []; }
        if (objects.IsDefaultOrEmpty && annots.IsDefaultOrEmpty) return false;

        // 놓을 자리: 개체와 주석이 함께 비키도록 둘의 경계 합집합으로 한 번 계산한다.
        var plan = await s.Thread.InvokeAsync(() =>
        {
            if (s.IndexOf(target) < 0) return ((PdfMatrix, PdfRectF)?)null;
            var union = Union(objects, annots);
            var box = PdfRectF.FromLtrbRectangle(s.GetLoadedPage(target).BoundsPt);
            return (PasteOffset.Compute(samePage, k, targetRotation, union, box), box);
        });
        if (plan is not { } pl) return false;   // 붙일 쪽이 그사이 지워짐: 조용히 무시
        var offset = pl.Item1;

        ImmutableArray<ObjectRef> newRefs = [];
        var newAnnotIds = new List<AnnotId>();
        try
        {
            if (!objects.IsDefaultOrEmpty)
            {
                var (edits, refs) = ObjectPasteBuilder.Build(s.Content, objects, offset);
                if (edits.Length > 0)
                {
                    await tab.RunContentCommandAsync(new ContentEditCommand(target, edits));
                    newRefs = refs;
                }
            }
            if (!annots.IsDefaultOrEmpty)
            {
                var cmd = AnnotationPasteBuilder.Build(target, targetRotation, entry.FromRotation, annots, offset,
                    () => { var id = s.Annotations.NewId(); newAnnotIds.Add(id); return id; }, Author(), Now());
                await tab.RunAnnotationCommandAsync(cmd);
            }
        }
        catch (Exception) { return !newRefs.IsDefaultOrEmpty; }   // 쪽이 그사이 지워짐 등

        // 붙인 새 개체·주석을 고른다.
        if (!newRefs.IsDefaultOrEmpty)
        {
            var picked = await s.Thread.InvokeAsync(() =>
            {
                if (s.IndexOf(target) < 0) return new List<SelectedObject>();
                var byRef = s.GetObjectIndex(target).Entries.ToDictionary(e => e.Ref);
                return newRefs.Where(byRef.ContainsKey).Select(r => new SelectedObject(target, r, byRef[r].Type, byRef[r].Bounds)).ToList();
            });
            tab.Objects.SelectMany(picked);
        }
        else tab.Objects.Clear();
        if (newAnnotIds.Count > 0) tab.Selection.Set(target, newAnnotIds);
        return true;
    }

    private static PdfRectF Union(ImmutableArray<ClipItem> objects, ImmutableArray<Annotation> annots)
    {
        PdfRectF? u = objects.IsDefaultOrEmpty ? null : PasteOffset.Union(objects);
        if (!annots.IsDefaultOrEmpty)
            foreach (var a in annots)
            {
                var r = AnnotationGeometry.ComputeRect(a);
                u = u is null ? r : new PdfRectF(Math.Min(u.Value.Left, r.Left), Math.Min(u.Value.Bottom, r.Bottom), Math.Max(u.Value.Right, r.Right), Math.Max(u.Value.Top, r.Top));
            }
        return u ?? default;
    }
}
