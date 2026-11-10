using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Document.Content;

/// <summary>
/// 개체 클립보드 항목(0.9.0). 복사 시점의 모양을 고정해 두므로 복사한 뒤 원래 개체를 옮기거나 지워도 붙이는 결과는 바뀌지 않는다.
/// Bounds = 복사 시점의 쪽 좌표 경계(놓을 자리 계산용).
/// </summary>
public abstract record ClipItem(PdfRectF Bounds);

/// <summary>원본 개체: 복사 시점까지 그 개체에 쌓인 TransformEdit를 접은 행렬(Then)과, 글자 색 편집이 있으면 그 색.</summary>
public sealed record OriginalClip(PageId SourcePage, int SourceIndex, PdfMatrix Then, PdfColor? TextColor, PdfRectF Bounds) : ClipItem(Bounds);

/// <summary>앱이 넣은 개체: 넣기 편집(도형은 최신 스타일을 반영한 사본)과, 그 개체에 쌓인 TransformEdit를 접은 행렬.</summary>
public sealed record InsertedClip(ContentEdit Recipe, PdfMatrix Then, PdfRectF Bounds) : ClipItem(Bounds);

/// <summary>CopiedRefs = Items[i]를 만든 객체(잘라내기가 이것만 지운다).</summary>
public sealed record CopyResult(ImmutableArray<ClipItem> Items, int Skipped, ImmutableArray<ObjectRef> CopiedRefs = default);

/// <summary>고른 개체 → 클립보드 항목. 복사할 수 없는 개체(문단 줄·찾아 바꾸기 조각·원래 글꼴로 다시 쓴 줄·화면 사본을 못 만드는 개체)는 Skipped.</summary>
public static class ObjectCopyBuilder
{
    /// <summary>PDFium 전용 스레드에서 부른다. 그리기 순서(PageObjectIndex Order)로 정렬해 돌려준다.</summary>
    public static CopyResult Build(DocumentSession s, PageId page, IReadOnlyList<ObjectRef> refs)
    {
        s.Thread.AssertOnThread();
        var edits = s.Content.For(page);
        var entries = s.GetObjectIndex(page).Entries.ToDictionary(e => e.Ref);
        var (source, life) = s.CreateDuplicateSource();
        var items = new List<(int Order, ClipItem Item, ObjectRef Ref)>();
        int skipped = 0;
        try
        {
            foreach (var r in refs.Distinct())
            {
                if (!entries.TryGetValue(r, out var entry)) { skipped++; continue; }
                var then = Fold(edits, r);
                var color = LastColor(edits, r);
                ClipItem? item = null;
                if (r.IsInserted)
                {
                    switch (edits.FirstOrDefault(e => e.Id == r.InsertedId))
                    {
                        case DuplicateObjectEdit du:
                        {
                            var o = source(du.SourcePage, du.SourceIndex);
                            if (o is not null && DuplicateScreen.CanDuplicate(o))
                                item = new OriginalClip(du.SourcePage, du.SourceIndex, du.Then.Then(then), color, entry.Bounds);   // 복제의 복제도 결국 원본 하나를 가리킨다
                            break;
                        }
                        case InsertShapeEdit sh:
                        {
                            var style = edits.OfType<SetShapeStyleEdit>().LastOrDefault(e => e.Target == r)?.Style ?? sh.Shape.Style;
                            item = new InsertedClip(sh with { Shape = sh.Shape with { Style = style } }, then, entry.Bounds);
                            break;
                        }
                        case InsertImageEdit or InsertAppTextEdit:
                            item = new InsertedClip(edits.First(e => e.Id == r.InsertedId), then, entry.Bounds);
                            break;
                    }
                }
                else if (!edits.OfType<SetShapeStyleEdit>().Any(e => e.Target == r))   // 원본 도형 스타일 바꾸기는 사본에 옮길 수 없다
                {
                    var o = source(page, r.OriginalIndex);
                    if (o is not null && DuplicateScreen.CanDuplicate(o))
                        item = new OriginalClip(page, r.OriginalIndex, then, o.Type == PdfPageObjectType.Text ? color : null, entry.Bounds);
                }
                if (item is null) skipped++; else items.Add((entry.Order, item, r));
            }
        }
        finally { life.Dispose(); }
        var sorted = items.OrderBy(x => x.Order).ToList();
        return new CopyResult([.. sorted.Select(x => x.Item)], skipped, [.. sorted.Select(x => x.Ref)]);
    }

    private static PdfMatrix Fold(IEnumerable<ContentEdit> edits, ObjectRef r)
    {
        var m = PdfMatrix.Identity;
        foreach (var e in edits) if (e is TransformEdit t && t.Target == r) m = m.Then(t.Then);
        return m;
    }

    private static PdfColor? LastColor(IEnumerable<ContentEdit> edits, ObjectRef r)
    {
        PdfColor? c = null;
        foreach (var e in edits) if (e is SetTextColorEdit t && t.Target == r) c = t.Color;
        return c;
    }
}

/// <summary>클립보드 항목 → 붙여넣기 편집. 항목 순서(= 원래 그리기 순서)대로 편집을 만든다. 한 쪽에 대한 ContentEditCommand 하나로 실행하면 실행 취소가 한 번이다.</summary>
public static class ObjectPasteBuilder
{
    public static (ContentEdit[] Edits, ImmutableArray<ObjectRef> NewRefs) Build(ContentEditStore store, IReadOnlyList<ClipItem> items, PdfMatrix offset)
    {
        var edits = new List<ContentEdit>();
        var refs = ImmutableArray.CreateBuilder<ObjectRef>();
        foreach (var item in items)
        {
            long id = store.NewId();
            switch (item)
            {
                case OriginalClip c:
                    edits.Add(new DuplicateObjectEdit(id, c.SourcePage, c.SourceIndex, c.Then.Then(offset)));
                    if (c.TextColor is { } tc) edits.Add(new SetTextColorEdit(store.NewId(), ObjectRef.Inserted(id), tc));
                    break;
                case InsertedClip { Recipe: InsertImageEdit im } c:
                    edits.Add(new InsertImageEdit(id, im.AssetId, im.Placement.Then(c.Then).Then(offset)));
                    break;
                case InsertedClip { Recipe: InsertShapeEdit sh } c:
                    edits.Add(new InsertShapeEdit(id, sh.Shape));
                    AddMove(edits, store, id, c.Then.Then(offset));
                    break;
                case InsertedClip { Recipe: InsertAppTextEdit at } c:
                    edits.Add(new InsertAppTextEdit(id, at.Line));
                    AddMove(edits, store, id, c.Then.Then(offset));
                    break;
                default:
                    continue;
            }
            refs.Add(ObjectRef.Inserted(id));
        }
        return (edits.ToArray(), refs.ToImmutable());
    }

    private static void AddMove(List<ContentEdit> edits, ContentEditStore store, long id, PdfMatrix m)
    {
        if (m != PdfMatrix.Identity) edits.Add(new TransformEdit(store.NewId(), ObjectRef.Inserted(id), m));
    }
}

/// <summary>붙여넣을 때 비키는 양(쪽 좌표 이동). 같은 쪽이면 화면 기준 오른쪽 아래로 10pt × k, 쪽 밖으로 나가면 안으로 당긴다.</summary>
public static class PasteOffset
{
    public const float StepPt = 10f;

    /// <param name="samePage">같은 쪽이면 비킨다. 다른 쪽이면 같은 쪽 좌표 그대로.</param>
    /// <param name="k">같은 클립보드를 이 쪽에 붙인 횟수(1부터).</param>
    /// <param name="pageRotation">PDFium 쪽 회전 0~3(90° 시계 방향 단위).</param>
    /// <param name="itemsBounds">붙일 개체들의 경계 합집합(쪽 좌표).</param>
    /// <param name="pageBox">붙일 쪽의 경계(쪽 좌표).</param>
    public static PdfMatrix Compute(bool samePage, int k, int pageRotation, PdfRectF itemsBounds, PdfRectF pageBox)
    {
        float dx = 0, dy = 0;
        if (samePage)
        {
            float s = StepPt * Math.Max(1, k);
            (dx, dy) = (((pageRotation % 4) + 4) % 4) switch
            {
                0 => (s, -s),
                1 => (s, s),
                2 => (-s, s),
                _ => (-s, -s),
            };
        }
        var moved = new PdfRectF(itemsBounds.Left + dx, itemsBounds.Bottom + dy, itemsBounds.Right + dx, itemsBounds.Top + dy);
        dx += Pull(moved.Left, moved.Right, pageBox.Left, pageBox.Right);
        dy += Pull(moved.Bottom, moved.Top, pageBox.Bottom, pageBox.Top);
        return PdfMatrix.Translate(dx, dy);
    }

    /// <summary>[lo, hi] 구간을 [boxLo, boxHi] 안으로 당기는 이동량. 구간이 더 길면 아래쪽(왼쪽)을 맞춘다.</summary>
    private static float Pull(float lo, float hi, float boxLo, float boxHi)
    {
        if (hi - lo >= boxHi - boxLo) return boxLo - lo;
        if (lo < boxLo) return boxLo - lo;
        if (hi > boxHi) return boxHi - hi;
        return 0;
    }

    /// <summary>항목들의 경계 합집합.</summary>
    public static PdfRectF Union(IEnumerable<ClipItem> items)
    {
        PdfRectF? u = null;
        foreach (var i in items)
            u = u is null ? i.Bounds : new PdfRectF(Math.Min(u.Value.Left, i.Bounds.Left), Math.Min(u.Value.Bottom, i.Bounds.Bottom), Math.Max(u.Value.Right, i.Bounds.Right), Math.Max(u.Value.Top, i.Bounds.Top));
        return u ?? default;
    }
}
