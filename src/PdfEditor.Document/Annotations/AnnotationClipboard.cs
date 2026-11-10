using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Commands;

namespace PdfEditor.Document.Annotations;

public sealed record AnnotationCopyResult(ImmutableArray<Annotation> Items, int Skipped);

/// <summary>
/// 고른 주석 → 클립보드 항목(0.9.0, 설계서 4.7). 텍스트 바꾸기 묶음(StrikeOut + Caret)은 묶음째 복사한다.
/// 복사하지 않는 것: 답글·검토 상태, 글자에 붙는 표시(Highlight·Underline·묶음이 아닌 StrikeOut — 글자와 떨어지면 뜻이 없다), 쪽에 없는 주석.
/// (링크·폼 필드·팝업·읽기 전용 원본은 모델에 편집 가능한 주석으로 들어오지 않으므로 고를 수 없다.)
/// </summary>
public static class AnnotationCopyBuilder
{
    public static AnnotationCopyResult Build(PageAnnotations page, IReadOnlyList<AnnotId> ids)
    {
        var order = page.Items.Select((v, i) => (v.Data.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var picked = new Dictionary<AnnotId, Annotation>();
        int skipped = 0;
        foreach (var id in ids.Distinct())
        {
            if (picked.ContainsKey(id)) continue;   // 앞서 고른 묶음의 구성원
            var group = page.GroupOf(id);
            if (group.Count == 0 || !Copyable(group)) { skipped++; continue; }
            foreach (var a in group) picked[a.Id] = a;
        }
        return new AnnotationCopyResult([.. picked.Values.OrderBy(a => order.GetValueOrDefault(a.Id, int.MaxValue))], skipped);
    }

    private static bool Copyable(IReadOnlyList<Annotation> group)
    {
        if (group.Count > 1) return group.All(a => a.Kind is AnnotKind.StrikeOut or AnnotKind.Caret);   // 바꾸기 묶음
        var a = group[0];
        if (a.InReplyTo is not null) return false;                       // 답글·검토 상태·묶음 없이 떨어진 Caret
        if (a.Kind is AnnotKind.Highlight or AnnotKind.Underline or AnnotKind.StrikeOut) return false;   // 글자에 붙는 표시
        return true;
    }
}

/// <summary>클립보드 항목 → 붙여넣기 명령(주석 모델만 바꾼다). 실행 취소 한 번에 모두 돌아온다.</summary>
public static class AnnotationPasteBuilder
{
    /// <param name="offset">이동만(E,F)이 쓰인다.</param>
    /// <param name="newId">새 주석 Id를 만든다(보통 <c>session.Annotations.NewId</c>).</param>
    public static IAnnotationCommand Build(PageId target, int targetPageRotation, int sourcePageRotation, IReadOnlyList<Annotation> items,
        PdfMatrix offset, Func<AnnotId> newId, string? author, DateTimeOffset now)
    {
        if (items.Count == 0) throw new ArgumentException("No annotations to paste.", nameof(items));
        var map = items.ToDictionary(a => a.Id, _ => newId());
        bool rotationChanged = (((targetPageRotation - sourcePageRotation) % 4) + 4) % 4 != 0;
        var changes = new List<AnnotationChange>(items.Count);
        foreach (var a in items)
        {
            var n = AnnotationGeometry.Translate(a, offset.E, offset.F) with
            {
                Id = map[a.Id],
                Name = Guid.NewGuid().ToString("N"),
                Author = author,
                CreatedAt = now,
                ModifiedAt = now,
                InReplyTo = a.InReplyTo is { } p && map.TryGetValue(p, out var np) ? np : null,
            };
            if (n.InReplyTo is null) n = n with { ReplyKind = null };
            if (rotationChanged && a.Kind is AnnotKind.FreeText or AnnotKind.Stamp) n = n with { Rotation = AnnotationRotation.ForPage(targetPageRotation) };
            changes.Add(new AnnotationChange(null, n));
        }
        return new ChangeAnnotationsCommand(target, changes);
    }
}
