using Pdf.Native;
using PdfEditor.Document.Annotations.Reading;

namespace PdfEditor.Document.Annotations;

/// <summary>
/// 편집할 수 없는 원본 주석(다른 앱 스탬프·링크 등)의 목록용 정보(M3b B2). 코멘트 목록이 회색으로 보여 주고 그 위치로 이동만 한다.
/// Index: 쪽의 /Annots 번호. Subtype: PdfAnnotSubtype 값(0 = 모르는 이름). Popup은 만들지 않는다.
/// </summary>
public sealed record ReadOnlyAnnotationInfo(
    int Index, int Subtype, PdfRectF Rect, string? Author, string? Contents, DateTimeOffset? ModifiedAt, ReadOnlyReason Reason)
{
    /// <summary>편집 가능했다가 읽기 전용으로 내려간 항목(Data가 있던 것)에서 만든다. Data가 없으면 리더가 채운 Info를 그대로 쓴다.</summary>
    public static ReadOnlyAnnotationInfo? From(OriginalAnnotation o, ReadOnlyReason reason)
        => o.Data is { } d
            ? new ReadOnlyAnnotationInfo(o.Index, o.Subtype, d.Rect, d.Author, d.Contents, d.ModifiedAt ?? d.CreatedAt, reason)
            : o.Info;
}
