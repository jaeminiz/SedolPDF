using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;

namespace PdfEditor.TextEdit.Paragraphs;

/// <summary>
/// 문단 추정의 입력 글 객체 하나(순수 데이터, PDFium 없음). 좌표는 쪽 좌표(pt, y 위쪽).
/// <paramref name="FontKey"/>는 글꼴 기본 이름 + 임베드 여부 + 글꼴 사전 식별(같은 글꼴만 같은 키).
/// <paramref name="Bounds"/>는 글 객체의 경계(PDFium 글 객체 경계 = 글자 잉크 범위), <paramref name="Origin"/>은 기준선 원점.
/// <paramref name="AngleDeg"/>는 쪽 좌표 기준 글 방향 각도(0·90·180·270만 편집 대상).
/// </summary>
public sealed record TextRun(ObjectRef Ref, string Text, PdfPointF Origin, int AngleDeg, float SizePt, string FontKey,
    PdfColor Color, float Tc, float Tz, PdfRectF Bounds, bool Visible);

public enum ObstacleKind { Line, Fill, Image, Text, Frame }

/// <summary>문단이 아래로 자랄 때 만나는 것(경로 선·칠·그림·다른 글·쪽 전체 테두리). <see cref="ObstacleKind.Frame"/>은 무시한다.</summary>
public sealed record Obstacle(PdfRectF Bounds, ObstacleKind Kind);
