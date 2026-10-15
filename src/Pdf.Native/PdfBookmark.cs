namespace Pdf.Native;

public sealed record PdfBookmark(string Title, int? PageIndex, IReadOnlyList<PdfBookmark> Children, bool InitiallyOpen);
