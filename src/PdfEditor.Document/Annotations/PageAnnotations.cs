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
}

public sealed record AnnotationsChangedEventArgs(IReadOnlyList<PageId> Pages);
