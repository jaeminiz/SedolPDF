using Pdf.Native;

namespace PdfEditor.Document.Annotations;

/// <summary>M2에서 만들고 편집하는 주석 종류.</summary>
public enum AnnotKind { Ink, Highlight, Underline, StrikeOut, Text, Square, Circle }

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
        _ => null,
    };

    public static bool IsTextMarkup(this AnnotKind k)
        => k is AnnotKind.Highlight or AnnotKind.Underline or AnnotKind.StrikeOut;
}
