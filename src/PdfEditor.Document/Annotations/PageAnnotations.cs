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
}

public sealed record AnnotationsChangedEventArgs(IReadOnlyList<PageId> Pages);
