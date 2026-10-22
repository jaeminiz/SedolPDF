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

public enum ShapeKind { Rectangle, Ellipse, Line }

/// <summary>도형 스타일. Stroke가 null이면 선 없음, Fill이 null이면 채우기 없음. 기본: 선 #E5372D 1.5pt, 채우기 없음(C5).</summary>
public sealed record ShapeStyle(PdfEditor.Document.Annotations.PdfColor? Stroke, float Width, PdfEditor.Document.Annotations.PdfColor? Fill)
{
    public static ShapeStyle Default { get; } = new(new PdfEditor.Document.Annotations.PdfColor(0xE5, 0x37, 0x2D), 1.5f, null);
}

/// <summary>새 도형. 사각형·타원은 Bounds, 선은 A → B(쪽 좌표).</summary>
public sealed record ShapeSpec(ShapeKind Kind, PdfRectF Bounds, PdfPointF A, PdfPointF B, ShapeStyle Style);

/// <summary>새 이미지(문서 자산 AssetId — PNG·JPEG)를 Placement(단위 사각형 → 쪽 좌표)에 쪽 맨 위로 넣는다.</summary>
public sealed record InsertImageEdit(long Id, string AssetId, PdfMatrix Placement) : ContentEdit(Id);

/// <summary>새 도형을 쪽 맨 위로 넣는다(PDFium 경로 객체).</summary>
public sealed record InsertShapeEdit(long Id, ShapeSpec Shape) : ContentEdit(Id);

/// <summary>도형(경로 객체)의 선·채우기 바꾸기.</summary>
public sealed record SetShapeStyleEdit(long Id, ObjectRef Target, ShapeStyle Style) : ContentEdit(Id);

/// <summary>
/// 문단 편집(M5)에서 원래 글꼴로 다시 쓰는 줄 하나. Placement = 쪽 좌표 글 객체 행렬(원본 글 객체 행렬에서 이동량(E,F)만 이 줄의 기준선 원점으로 바꾼 것).
/// OriginalFontSource = 글꼴·색·렌더 모드를 가져올 원본 글 객체(원본 번호). LineId는 이 줄을 가리킬 때 쓰는 편집 Id(<see cref="ObjectRef.Inserted"/>).
/// Tc·Tz·JustifyExtra·OrigRenderMode는 저장(직접 인코딩, Task 6)이 쓴다 — 화면 replay의 PDFium 글 객체는 자간·맞춤 늘임을 그리지 못한다(한계).
/// </summary>
public sealed record ParagraphTextLine(long LineId, string Text, PdfMatrix Placement, float SizePt, PdfEditor.Document.Annotations.PdfColor Color,
    ObjectRef OriginalFontSource, float Tc, float Tz, float JustifyExtra, int OrigRenderMode,
    byte[]? Encoded = null, bool TwoByte = false);
// Encoded = 원래 글꼴 코드북으로 인코딩한 글 코드 바이트(계획 때 정함 — 저장은 PDFium 없이 이것을 그대로 쓴다). null이면 직접 쓰기 불가(PDFium 경로로 내려간다).

/// <summary>다시 편집(원본 문단 재추정 없이)에 쓰는 문단 정보. Model은 TextEdit의 ParagraphModel(Document는 TextEdit를 모르므로 불투명 개체로 보관).</summary>
public sealed record ParagraphMeta(PdfRectF Box, string OriginalText, string NewText, string Align, string FontKey, float Leading, object? Model);

/// <summary>
/// 문단 편집 하나(M5 C14): 끄기 대상 원본 객체들 + 새 줄(원래 글꼴 / 앱 글꼴) + 메타를 한 묶음으로.
/// 화면 replay는 Remove를 끄고 새 줄을 넣는다. 출력(Output) replay는 끄기만 하고 새 줄은 내용 스트림 경로(Task 6)가 쓴다.
/// AppLineIds[i]는 AppLines[i]를 가리키는 Id.
/// </summary>
public sealed record ReplaceParagraphEdit(long Id, System.Collections.Immutable.ImmutableArray<ObjectRef> Remove,
    System.Collections.Immutable.ImmutableArray<ParagraphTextLine> OriginalFontLines,
    System.Collections.Immutable.ImmutableArray<AppTextSpec> AppLines, System.Collections.Immutable.ImmutableArray<long> AppLineIds,
    ParagraphMeta Meta) : ContentEdit(Id);
