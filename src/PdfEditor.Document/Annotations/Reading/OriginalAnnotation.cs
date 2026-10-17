namespace PdfEditor.Document.Annotations.Reading;

/// <summary>파일에 원래 있던 주석을 편집할 수 없는 이유 (None이면 편집 가능).</summary>
public enum ReadOnlyReason
{
    None,
    UnsupportedType,
    Reply,
    ReviewState,
    CloudBorder,
    StyledBorder,
    RectDifferences,
    HiddenFlag,
    Malformed,
    /// <summary>선 끝 모양 이름을 모른다(ROpenArrow 등). 그리지 못하므로 읽기 전용.</summary>
    UnsupportedLineEnding,
    /// <summary>선의 지시선(/LL)·캡션(/Cap)·/Measure가 있다. 모델이 다루지 못해 손대면 사라지므로 읽기 전용.</summary>
    UnsupportedLineFeatures,
    /// <summary>원본 스탬프·파일 첨부 주석: 우리 Painter가 다시 그릴 수 없으므로 읽기 전용이고 화면은 PDFium이 원본 AP를 그린다.</summary>
    OriginalAppearanceOnly,
}

/// <summary>
/// 원본 주석 하나. Index는 쪽의 /Annots 배열 순서(= PDFium 주석 번호).
/// Data는 Reason이 None일 때만 있으며 Data.Id는 default(문서에 붙일 때 정한다).
/// GroupParentIndex: 텍스트 바꾸기 묶음의 Caret이면 부모 StrikeOut의 /Annots 번호(문서에 붙일 때 InReplyTo로 바꾼다).
/// ReplyParentIndex: 답글·검토 상태(Text + /IRT)이면 부모의 /Annots 번호(부모가 이 쪽에서 편집 가능할 때만 편집 가능한 항목이 된다).
/// </summary>
public sealed record OriginalAnnotation(int Index, int Subtype, Annotation? Data, ReadOnlyReason Reason, int? GroupParentIndex = null, int? ReplyParentIndex = null);

/// <summary>원본 주석을 읽을 문서를 열 수 없을 때.</summary>
public sealed class AnnotationReadException(string message, Exception? inner) : Exception(message, inner);
