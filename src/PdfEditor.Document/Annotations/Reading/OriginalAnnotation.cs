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
}

/// <summary>
/// 원본 주석 하나. Index는 쪽의 /Annots 배열 순서(= PDFium 주석 번호).
/// Data는 Reason이 None일 때만 있으며 Data.Id는 default(문서에 붙일 때 정한다).
/// </summary>
public sealed record OriginalAnnotation(int Index, int Subtype, Annotation? Data, ReadOnlyReason Reason);

/// <summary>원본 주석을 읽을 문서를 열 수 없을 때.</summary>
public sealed class AnnotationReadException(string message, Exception? inner) : Exception(message, inner);
