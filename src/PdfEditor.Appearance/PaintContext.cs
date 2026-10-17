using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

/// <summary>Painter가 글자 배치(앱 글꼴)와 그림(AnnotationAssets)을 쓸 때 필요한 것. 화면과 PDF가 같은 글꼴을 써야 글자 위치가 같다.</summary>
public sealed record PaintContext(AppFont Font, AnnotationAssets Assets);
