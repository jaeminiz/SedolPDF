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

/// <summary>앱 글꼴 줄의 윤곽선 한 토막(쪽 좌표). Op: 'M' 이동, 'L' 직선, 'C' 3차 베지어(A·B 제어점, C 끝점), 'Z' 닫기.</summary>
public readonly record struct OutlineSeg(char Op, PdfPointF A, PdfPointF B = default, PdfPointF C = default);

/// <summary>
/// 앱 글꼴(Noto Sans KR) 한 줄(C6). 화면은 Outline(같은 글리프 윤곽선, 쪽 좌표)을 경로 객체로 그리고,
/// 파일은 패치 단계가 Content(글리프 CID로 쓴 BT…ET, 글꼴 자원 이름은 자리표시 "{F}")와 Font(서브셋 재료)로 진짜 글자를 쓴다.
/// Placement: 기준선 왼쪽 점(0,0)·글자 크기 1 기준 로컬 → 쪽 좌표 행렬(원래 줄의 텍스트 행렬). Content·Outline·Bounds는 이미 쪽 좌표다
/// (나중의 TransformEdit는 패치 계획이 cm으로 감싼다).
/// </summary>
public sealed record AppTextSpec(string Text, float SizePt, PdfEditor.Document.Annotations.PdfColor Color, PdfMatrix Placement, PdfRectF Bounds,
    System.Collections.Immutable.ImmutableArray<OutlineSeg> Outline, string Content,
    PdfEditor.Document.Annotations.Patching.FontUse Font);

/// <summary>앱 글꼴 줄을 쪽 맨 위에 넣는다(C7: 화면 replay도 다른 편집을 모두 적용한 뒤 맨 마지막에). 가리킬 때는 ObjectRef.Inserted(Id).</summary>
public sealed record InsertAppTextEdit(long Id, AppTextSpec Line) : ContentEdit(Id);
