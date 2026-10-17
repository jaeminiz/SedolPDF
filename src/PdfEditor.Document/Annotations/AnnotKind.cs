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
        _ => null,
    };

    /// <summary>PDFium이 만들 수 없어 저장 때 PDFsharp 패치로 쓰는 유형(계획 A3).</summary>
    public static bool IsPatchKind(this AnnotKind k)
        => k is AnnotKind.Line or AnnotKind.PolyLine or AnnotKind.Polygon or AnnotKind.FreeText
            or AnnotKind.Caret or AnnotKind.Stamp or AnnotKind.FileAttachment;

    public static bool IsTextMarkup(this AnnotKind k)
        => k is AnnotKind.Highlight or AnnotKind.Underline or AnnotKind.StrikeOut;
}
