using PdfEditor.Document.Commands;

namespace PdfEditor.Document.Annotations;

/// <summary>Before null = 추가, After null = 삭제, 둘 다 = 교체.</summary>
public readonly record struct AnnotationChange(Annotation? Before, Annotation? After);

/// <summary>한 쪽의 주석을 모델에서 바꾸는 명령 (PDFium은 저장·인쇄 때 Flush로 반영).</summary>
public sealed class ChangeAnnotationsCommand(PageId page, IReadOnlyList<AnnotationChange> changes) : IAnnotationCommand
{
    private readonly AnnotationChange[] _changes = [.. changes];

    public static ChangeAnnotationsCommand Add(PageId page, Annotation a)
        => new(page, [new AnnotationChange(null, a)]);

    public static ChangeAnnotationsCommand Delete(PageId page, IReadOnlyList<Annotation> items)
        => new(page, [.. items.Select(a => new AnnotationChange(a, null))]);

    public static ChangeAnnotationsCommand Replace(PageId page, IReadOnlyList<(Annotation Before, Annotation After)> items)
        => new(page, [.. items.Select(x => new AnnotationChange(x.Before, x.After))]);

    public void Execute(DocumentSession s)
    {
        int done = 0;
        try
        {
            for (; done < _changes.Length; done++)
                s.Annotations.Apply(page, _changes[done].Before, _changes[done].After);
        }
        catch
        {
            // 중간 실패: 적용한 것을 거꾸로 되돌린다
            for (int i = done - 1; i >= 0; i--)
                s.Annotations.Apply(page, _changes[i].After, _changes[i].Before);
            throw;
        }
    }

    public void Undo(DocumentSession s)
    {
        for (int i = _changes.Length - 1; i >= 0; i--)
            s.Annotations.Apply(page, _changes[i].After, _changes[i].Before);
    }
}
