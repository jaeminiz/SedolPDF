using System.Collections.Immutable;

namespace PdfEditor.Document.Annotations;

/// <summary>
/// 화면에 보이는 주석 하나. ModelDrawn: 우리 그리기 층이 그린다(새 주석, 손댄 원본).
/// false이면 손대지 않은 원본이라 PDFium이 그리며, 선택·편집은 가능하다.
/// </summary>
public sealed record AnnotationView(Annotation Data, bool ModelDrawn);

/// <summary>한 쪽의 주석 스냅숏. 그리는 순서(원본은 PDF 순서, 새 주석은 만든 순서로 뒤에), 삭제된 것 제외.</summary>
public sealed record PageAnnotations(PageId Page, ImmutableArray<AnnotationView> Items)
{
    /// <summary>편집할 수 없는 원본 주석의 목록용 정보(Popup 제외). 코멘트 목록이 쓴다.</summary>
    public ImmutableArray<ReadOnlyAnnotationInfo> ReadOnly { get; init; } = [];

    public static PageAnnotations Empty(PageId page) => new(page, []);

    /// <summary>
    /// 텍스트 바꾸기 묶음(StrikeOut + Caret, A11): 대표(StrikeOut)가 맨 앞, 이어서 그것을 Group으로 가리키는 항목들.
    /// 묶이지 않은 주석은 자기 하나만, 없는 주석은 빈 목록.
    /// </summary>
    public IReadOnlyList<Annotation> GroupOf(AnnotId id)
    {
        var self = Items.FirstOrDefault(v => v.Data.Id == id)?.Data;
        if (self is null) return [];
        var rep = self.InReplyTo is { } pid && self.ReplyKind == ReplyType.Group
            ? Items.FirstOrDefault(v => v.Data.Id == pid)?.Data ?? self
            : self;
        var list = new List<Annotation> { rep };
        foreach (var v in Items)
            if (v.Data.Id != rep.Id && v.Data.InReplyTo == rep.Id && v.Data.ReplyKind == ReplyType.Group) list.Add(v.Data);
        return list;
    }

    /// <summary>
    /// 뿌리 + 그 아래 모든 답글(시각순) + 상태(시각순). 답글에 단 답글도 포함한다. 뿌리가 없으면 빈 목록.
    /// </summary>
    public IReadOnlyList<Annotation> ThreadOf(AnnotId root)
    {
        var self = Items.FirstOrDefault(v => v.Data.Id == root)?.Data;
        if (self is null) return [];
        var replies = new List<Annotation>();
        var states = new List<Annotation>();
        var seen = new HashSet<AnnotId> { root };
        var queue = new Queue<AnnotId>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var v in Items)
            {
                var d = v.Data;
                if (d.InReplyTo != parent || d.ReplyKind == ReplyType.Group || !d.IsThreadMember() || !seen.Add(d.Id)) continue;
                (d.State is null ? replies : states).Add(d);
                queue.Enqueue(d.Id);
            }
        }
        var list = new List<Annotation>(1 + replies.Count + states.Count) { self };
        list.AddRange(replies.OrderBy(Stamp).ThenBy(a => a.Id.Value));
        list.AddRange(states.OrderBy(Stamp).ThenBy(a => a.Id.Value));
        return list;
    }

    /// <summary>주석 자신에게 직접 단 상태 중 가장 최근(수정 시각) 것. 없으면 None.</summary>
    public ReviewState CurrentState(AnnotId root)
    {
        Annotation? latest = null;
        foreach (var v in Items)
        {
            var d = v.Data;
            if (d.InReplyTo != root || d.State is null || !d.IsThreadMember()) continue;
            if (latest is null || Stamp(d) >= Stamp(latest)) latest = d;   // 같은 시각이면 나중에 만든(뒤에 있는) 것
        }
        return latest?.State ?? ReviewState.None;
    }

    private static DateTimeOffset Stamp(Annotation a) => a.ModifiedAt ?? a.CreatedAt ?? DateTimeOffset.MinValue;

    /// <summary>
    /// 지울 때 함께 지울 항목 전부: 묶음(GroupOf) 구성원과 각각의 답글·상태. 대표(있으면)가 맨 앞이고 중복은 없다.
    /// App은 부모를 지우는 명령에 이 목록을 그대로 담는다(Review Focus 2).
    /// </summary>
    public IReadOnlyList<Annotation> DeleteSetOf(AnnotId id)
    {
        var result = new List<Annotation>();
        var seen = new HashSet<AnnotId>();
        foreach (var member in GroupOf(id))
            foreach (var t in ThreadOf(member.Id))
                if (seen.Add(t.Id)) result.Add(t);
        return result;
    }
}

public sealed record AnnotationsChangedEventArgs(IReadOnlyList<PageId> Pages);
