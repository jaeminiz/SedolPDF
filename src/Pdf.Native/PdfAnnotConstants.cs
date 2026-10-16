namespace Pdf.Native;

/// <summary>PDF 주석 Subtype 번호 (PDFium FPDF_ANNOT_* 값).</summary>
public static class PdfAnnotSubtype
{
    public const int Text = 1, Link = 2, FreeText = 3, Line = 4, Square = 5, Circle = 6, Polygon = 7, PolyLine = 8,
        Highlight = 9, Underline = 10, Squiggly = 11, StrikeOut = 12, Stamp = 13, Caret = 14, Ink = 15, Popup = 16,
        FileAttachment = 17, Widget = 20;
}

/// <summary>주석 /F 플래그 비트.</summary>
public static class PdfAnnotFlags
{
    public const int Invisible = 1, Hidden = 2, Print = 4, NoZoom = 8, NoRotate = 16;
}

/// <summary>FPDFAnnot_SetColor가 쓰는 색 종류.</summary>
public enum PdfColorType
{
    Color = 0,
    Interior = 1,
}
