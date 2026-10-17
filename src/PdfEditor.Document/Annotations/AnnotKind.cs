using Pdf.Native;

namespace PdfEditor.Document.Annotations;

/// <summary>만들고 편집하는 주석 종류. M2: 앞 7개(PDFium 길), M3: 뒤 7개(패치 길, 계획 A3).</summary>
public enum AnnotKind
{
    Ink, Highlight, Underline, StrikeOut, Text, Square, Circle,
    Line, PolyLine, Polygon, FreeText, Caret, Stamp, FileAttachment,
}

public static class AnnotKindExtensions
{
    public static int ToPdfSubtype(this AnnotKind k) => k switch
    {
        AnnotKind.Ink => PdfAnnotSubtype.Ink,
        AnnotKind.Highlight => PdfAnnotSubtype.Highlight,
        AnnotKind.Underline => PdfAnnotSubtype.Underline,
        AnnotKind.StrikeOut => PdfAnnotSubtype.StrikeOut,
        AnnotKind.Text => PdfAnnotSubtype.Text,
        AnnotKind.Square => PdfAnnotSubtype.Square,
        AnnotKind.Circle => PdfAnnotSubtype.Circle,
        AnnotKind.Line => PdfAnnotSubtype.Line,
        AnnotKind.PolyLine => PdfAnnotSubtype.PolyLine,
        AnnotKind.Polygon => PdfAnnotSubtype.Polygon,
        AnnotKind.FreeText => PdfAnnotSubtype.FreeText,
        AnnotKind.Caret => PdfAnnotSubtype.Caret,
        AnnotKind.Stamp => PdfAnnotSubtype.Stamp,
        AnnotKind.FileAttachment => PdfAnnotSubtype.FileAttachment,
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    public static AnnotKind? FromPdfSubtype(int subtype) => subtype switch
    {
        PdfAnnotSubtype.Ink => AnnotKind.Ink,
        PdfAnnotSubtype.Highlight => AnnotKind.Highlight,
        PdfAnnotSubtype.Underline => AnnotKind.Underline,
        PdfAnnotSubtype.StrikeOut => AnnotKind.StrikeOut,
        PdfAnnotSubtype.Text => AnnotKind.Text,
        PdfAnnotSubtype.Square => AnnotKind.Square,
        PdfAnnotSubtype.Circle => AnnotKind.Circle,
        PdfAnnotSubtype.FreeText => AnnotKind.FreeText,
        PdfAnnotSubtype.Line => AnnotKind.Line,
        PdfAnnotSubtype.PolyLine => AnnotKind.PolyLine,
        PdfAnnotSubtype.Polygon => AnnotKind.Polygon,
        PdfAnnotSubtype.Caret => AnnotKind.Caret,
        _ => null,
    };

    /// <summary>PDFium이 만들 수 없어 저장 때 PDFsharp 패치로 쓰는 유형(계획 A3).</summary>
    public static bool IsPatchKind(this AnnotKind k)
        => k is AnnotKind.Line or AnnotKind.PolyLine or AnnotKind.Polygon or AnnotKind.FreeText
            or AnnotKind.Caret or AnnotKind.Stamp or AnnotKind.FileAttachment;

    /// <summary>
    /// 저장 때 PDFium이 아니라 PDFsharp 패치로 쓰는 주석인가: 패치 길 유형이거나, 답글·상태·묶음처럼 /IRT를 가진 주석
    /// (답글·상태 Text를 PDFium이 만들면 /IRT가 없는 일반 메모가 되어 버린다).
    /// </summary>
    public static bool NeedsPatch(this Annotation a) => a.Kind.IsPatchKind() || a.InReplyTo is not null;

    /// <summary>답글 또는 검토 상태 주석(Text + /IRT, 묶음 Caret 제외)인가.</summary>
    public static bool IsThreadMember(this Annotation a) => a.Kind == AnnotKind.Text && a.InReplyTo is not null;

    public static bool IsTextMarkup(this AnnotKind k)
        => k is AnnotKind.Highlight or AnnotKind.Underline or AnnotKind.StrikeOut;
}
