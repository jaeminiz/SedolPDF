using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

public sealed class PdfAppearanceSource : IAppearanceSource
{
    public string BuildContent(Annotation a, int pageRotation)
        => PdfContentWriter.Write(AnnotationPainter.Paint(a, pageRotation));
}
