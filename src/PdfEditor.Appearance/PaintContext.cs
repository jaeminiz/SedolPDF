using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

/// <summary>
/// Painter가 글자 배치(앱 글꼴)와 그림(AnnotationAssets)을 쓸 때 필요한 것. 화면과 PDF가 같은 글꼴을 써야 글자 위치가 같다.
/// 글꼴은 공급자(Func)로도 줄 수 있다: 글자가 있는 주석을 그릴 때 처음으로 <see cref="Font"/>를 읽으므로,
/// 앱은 글꼴 파일(4.6 MB)을 시작 때가 아니라 그때 읽는다.
/// </summary>
public sealed class PaintContext
{
    private readonly AppFont? _font;
    private readonly Func<AppFont?>? _fontProvider;

    public PaintContext(AppFont? font, AnnotationAssets assets)
    {
        _font = font;
        Assets = assets;
    }

    private PaintContext(Func<AppFont?> fontProvider, AnnotationAssets assets, bool _)
    {
        _fontProvider = fontProvider;
        Assets = assets;
    }

    /// <summary>글꼴을 글자가 필요할 때 공급자에게서 받는 컨텍스트.</summary>
    public static PaintContext WithFontProvider(Func<AppFont?> fontProvider, AnnotationAssets assets) => new(fontProvider, assets, false);

    /// <summary>글꼴. 공급자가 있으면 읽을 때마다 공급자를 부른다(공급자가 캐시한다).</summary>
    public AppFont? Font => _font ?? _fontProvider?.Invoke();

    public AnnotationAssets Assets { get; }
}
