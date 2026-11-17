using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

/// <summary>
/// 주석 글자에 쓸 글꼴을 고른 결과(0.12.0). Key는 TextRunOp.Family·FontUse.Family에 들어가 화면·AP·저장이 같은 글꼴을 찾게 한다.
/// SyntheticBold/Italic: 가짜 굵게·기울임을 켤지(진짜 굵은 얼굴을 찾았으면 끈다). Substituted: 원하는 시스템 글꼴 대신 Noto로 그렸는가.
/// </summary>
public sealed record ResolvedFont(AppFont Font, string Key, bool SyntheticBold, bool SyntheticItalic, bool Substituted);

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

    private PaintContext(Func<AppFont?> fontProvider, AnnotationAssets assets, Func<AppFont?>? serifProvider, SystemFontLibrary? systemFonts)
    {
        SystemFonts = systemFonts;
        _fontProvider = fontProvider;
        _serifProvider = serifProvider;
        Assets = assets;
    }

    /// <summary>글꼴을 글자가 필요할 때 공급자에게서 받는 컨텍스트. 명조 공급자는 명조를 고른 글자가 있을 때만 부른다.</summary>
    public static PaintContext WithFontProvider(Func<AppFont?> fontProvider, AnnotationAssets assets, Func<AppFont?>? serifProvider = null, SystemFontLibrary? systemFonts = null)
        => new(fontProvider, assets, serifProvider, systemFonts);

    /// <summary>설치된 글꼴(0.12.0). null이면 FontName이 있어도 Noto로 그린다(시험·글꼴 없는 환경).</summary>
    public SystemFontLibrary? SystemFonts { get; init; }

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

    /// <summary>
    /// FreeText 글자의 글꼴(0.12.0): FontName이 설치되어 있고 PDF에 넣을 수 있으면 그 글꼴(굵기·기울임이 가장 가까운 얼굴),
    /// 아니면 TextFamily의 Noto. 앱 글꼴도 없으면 null.
    /// </summary>
    public ResolvedFont? Resolve(Annotation a)
    {
        // 고른 글꼴에 없는 글자가 있으면(예: 영문 글꼴에 한글) 그 글자가 빠지지 않게 상자 전체를 Noto로 그린다. 이름은 그대로 둔다.
        if (a.FontName is { Length: > 0 } name && SystemFonts is { } lib
            && lib.Find(name, a.Bold, a.Italic) is { } face && lib.Load(face) is { } sys && Covers(sys, a.Contents))
            return new ResolvedFont(sys, face.Key, a.Bold && !face.Info.IsBold, a.Italic && !face.Info.Italic, false);
        return FontFor(a.TextFamily, out string family) is { } f
            ? new ResolvedFont(f, family, a.Bold, a.Italic, Substituted: a.FontName is { Length: > 0 } && SystemFonts is not null)
            : null;
    }

    /// <summary>글꼴에 글(공백·제어 문자 빼고)의 모든 글자가 있는가.</summary>
    public static bool Covers(AppFont font, string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        foreach (var r in text.EnumerateRunes())
        {
            if (System.Text.Rune.IsWhiteSpace(r) || System.Text.Rune.IsControl(r)) continue;
            if (font.GetGlyph(r.Value) is not > 0) return false;
        }
        return true;
    }

    /// <summary>글꼴 키(TextRunOp.Family)의 글꼴: ""(고딕), "serif"(명조, 없으면 고딕), "sys:…"(설치된 글꼴).</summary>
    public AppFont? FontForKey(string? key) => FontForKey(key, () => Font, () => SerifFont, SystemFonts);

    /// <summary>화면(AppearanceGeometry)·패처가 같은 규칙으로 쓰는 키 해석. 글꼴 공급자는 그 키가 필요할 때만 부른다.</summary>
    public static AppFont? FontForKey(string? key, Func<AppFont?> sans, Func<AppFont?> serif, SystemFontLibrary? systemFonts)
    {
        if (string.IsNullOrEmpty(key)) return sans();
        if (key == PdfEditor.Document.Annotations.Patching.FontUse.SerifFamily) return serif() ?? sans();
        return systemFonts?.FontForKey(key);
    }

    /// <summary>글꼴. 공급자가 있으면 읽을 때마다 공급자를 부른다(공급자가 캐시한다).</summary>
    public AppFont? Font => _font ?? _fontProvider?.Invoke();

    public AnnotationAssets Assets { get; }
}
