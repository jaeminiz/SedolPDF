using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Viewer.Annotations;

/// <summary>도구로 만들 주석의 모양. Fill은 사각형·타원만 쓴다.</summary>
public sealed record ToolStyle(PdfColor Color, PdfColor? Fill, float Opacity, float Width);
