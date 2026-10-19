using Pdf.Native;

namespace PdfEditor.Document.Content;

/// <summary>
/// 쪽 객체를 가리킨다. 원본 객체는 원본 내용 스트림의 파싱 순서 번호(OriginalIndex ≥ 0)로, 편집으로 넣은 객체는 그 편집의 Id(InsertedId)로.
/// 원본 내용은 세션 동안 바뀌지 않으므로 원본 번호는 영원히 같다(M4 계획 C1·C2, V12).
/// </summary>
public readonly record struct ObjectRef(int OriginalIndex, long InsertedId)
{
    public static ObjectRef Original(int index) => new(index, 0);
    public static ObjectRef Inserted(long editId) => new(-1, editId);
    public bool IsInserted => InsertedId != 0;
}

/// <summary>쪽 객체 편집 하나. 쪽마다 순서대로 쌓이고, 쪽을 불러올 때마다 이 순서로 다시 적용된다.</summary>
public abstract record ContentEdit(long Id);

/// <summary>객체 끄기(지우기). 객체는 메모리에 남고 그리기·저장에서 빠진다(C3).</summary>
public sealed record DeactivateEdit(long Id, ObjectRef Target) : ContentEdit(Id);

/// <summary>객체의 행렬 뒤에 Then을 곱한다(쪽 좌표에서 옮기기·크기 바꾸기).</summary>
public sealed record TransformEdit(long Id, ObjectRef Target, PdfMatrix Then) : ContentEdit(Id);

/// <summary>
/// 원래 글꼴로 만든 새 텍스트 객체를 원래 객체 바로 뒤에 넣는다(C4 ①). 원래 객체를 끄는 것은 따로 <see cref="DeactivateEdit"/>로 쌓는다.
/// 넣은 객체는 <see cref="ObjectRef.Inserted"/>(이 편집의 Id)로 가리킨다.
/// </summary>
public sealed record InsertTextLikeEdit(long Id, ObjectRef Original, string Text) : ContentEdit(Id);
