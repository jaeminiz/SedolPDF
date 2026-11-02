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
    private readonly AppFont? _serif;
    private readonly Func<AppFont?>? _serifProvider;

    public PaintContext(AppFont? font, AnnotationAssets assets, AppFont? serif = null)
    {
        _font = font;
        _serif = serif;
        Assets = assets;
    }

    private PaintContext(Func<AppFont?> fontProvider, AnnotationAssets assets, Func<AppFont?>? serifProvider)
    {
        _fontProvider = fontProvider;
        _serifProvider = serifProvider;
        Assets = assets;
    }

    /// <summary>글꼴을 글자가 필요할 때 공급자에게서 받는 컨텍스트. 명조 공급자는 명조를 고른 글자가 있을 때만 부른다.</summary>
    public static PaintContext WithFontProvider(Func<AppFont?> fontProvider, AnnotationAssets assets, Func<AppFont?>? serifProvider = null)
        => new(fontProvider, assets, serifProvider);

    /// <summary>명조 글꼴(0.5.0). 없으면 null — <see cref="FontFor"/>가 고딕으로 물러난다.</summary>
    public AppFont? SerifFont => _serif ?? _serifProvider?.Invoke();

    /// <summary>
    /// 글꼴 가족에 맞는 글꼴. 명조를 골랐는데 명조 파일이 없으면 고딕으로 물러나고 <paramref name="family"/>가 ""(고딕)이 된다:
    /// 화면과 AP가 같은 글꼴을 쓰도록 TextRunOp.Family·FontUse.Family에 이 값을 쓴다.
    /// </summary>
    public AppFont? FontFor(TextFontFamily wanted, out string family)
    {
        if (wanted == TextFontFamily.Serif && SerifFont is { } s) { family = PdfEditor.Document.Annotations.Patching.FontUse.SerifFamily; return s; }
        family = "";
        return Font;
    }

    /// <summary>글꼴. 공급자가 있으면 읽을 때마다 공급자를 부른다(공급자가 캐시한다).</summary>
    public AppFont? Font => _font ?? _fontProvider?.Invoke();

    public AnnotationAssets Assets { get; }
}
