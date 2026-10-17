using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

/// <summary>
/// 주석 AP 내용을 만든다. font는 글자가 있는 유형(FreeText)에만 필요하다 — 없으면 그 유형은 InvalidOperationException.
/// 앱은 화면(AppearanceGeometry.TextFont)과 같은 AppFont 하나를 넘긴다.
/// </summary>
public sealed class PdfAppearanceSource : IAppearanceSource
{
    private readonly PaintContext _ctx;

    public PdfAppearanceSource(AppFont? font = null, AnnotationAssets? assets = null)
        => _ctx = new PaintContext(font, assets ?? AnnotationAssets.Empty);

    private PdfAppearanceSource(PaintContext ctx) => _ctx = ctx;

    /// <summary>글꼴을 글자가 있는 주석을 처음 만들 때 읽는다(앱 시작을 늦추지 않기 위해).</summary>
    public static PdfAppearanceSource WithFontProvider(Func<AppFont?> fontProvider, AnnotationAssets? assets = null)
        => new(PaintContext.WithFontProvider(fontProvider, assets ?? AnnotationAssets.Empty));

    public string BuildContent(Annotation a, int pageRotation)
        => PdfContentWriter.Write(AnnotationPainter.Paint(a, pageRotation, _ctx));

    public PatchAppearance BuildPatchAppearance(Annotation a, int pageRotation)
    {
        var d = AnnotationPainter.Paint(a, pageRotation, _ctx);
        return new PatchAppearance(PdfContentWriter.Write(d), a.Rect, d.CollectFontUse());
    }
}
