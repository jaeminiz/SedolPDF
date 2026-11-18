using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

/// <summary>
/// 주석 글자에 쓸 글꼴을 고른 결과(0.12.0). Key는 TextRunOp.Family·FontUse.Family에 들어가 화면·AP·저장이 같은 글꼴을 찾게 한다.
/// SyntheticBold/Italic: 가짜 굵게·기울임을 켤지(진짜 굵은 얼굴을 찾았으면 끈다). Substituted: 원하는 시스템 글꼴 대신 Noto로 그렸는가.
/// </summary>
public sealed record ResolvedFont(AppFont Font, string Key, bool SyntheticBold, bool SyntheticItalic, bool Substituted, ResolvedFont? Fallback = null)
{
    /// <summary>
    /// 글자별 대체(0.13.0): 고른 글꼴이 글을 일부만 덮으면 Fallback이 그 글꼴에 없는 글자를 맡는 Noto다(Fallback.Fallback은 null).
    /// 배치·그리기·저장은 이 목록의 자리 번호(PositionedGlyph.FontIndex)로 글꼴을 찾는다. 글이 한 글꼴로 되면 Fallback은 null이다.
    /// </summary>
    public IReadOnlyList<ResolvedFont> Chain => Fallback is null ? [this] : [this, Fallback];

    public IReadOnlyList<AppFont> Fonts => Fallback is null ? [Font] : [Font, Fallback.Font];
}

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
        // 고른 글꼴이 글을 다 덮으면 그 글꼴. 일부만 덮으면(예: 영문 글꼴에 한글) 가진 글자는 그 글꼴, 없는 글자만 Noto(글자별 대체, 0.13.0).
        // 하나도 못 덮으면(예: 한글만) 상자 전체를 Noto로 그린다. 이름은 어느 경우든 그대로 둔다.
        if (a.FontName is { Length: > 0 } name && SystemFonts is { } lib
            && lib.Find(name, a.Bold, a.Italic) is { } face && lib.Load(face) is { } sys && CoversAny(sys, a.Contents))
        {
            bool all = Covers(sys, a.Contents);
            ResolvedFont? fb = all ? null
                : FontFor(a.TextFamily, out string fbFamily) is { } nf ? new ResolvedFont(nf, fbFamily, a.Bold, a.Italic, true) : null;
            if (all || fb is not null)
                return new ResolvedFont(sys, face.Key, a.Bold && !face.Info.IsBold, a.Italic && !face.Info.Italic, false, fb);
        }
        return FontFor(a.TextFamily, out string family) is { } f
            ? new ResolvedFont(f, family, a.Bold, a.Italic, Substituted: a.FontName is { Length: > 0 } && SystemFonts is not null)
            : null;
    }

    /// <summary>글꼴이 글의 글자(공백·문장부호·제어 문자 빼고) 가운데 하나라도 덮는가. 문장부호만 덮는 글꼴은 글자별 대체를 하지 않는다. 글자가 하나도 없는 글은 참.</summary>
    public static bool CoversAny(AppFont font, string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        bool hasStrong = false;
        foreach (var r in text.EnumerateRunes())
        {
            if (System.Text.Rune.IsControl(r) || Fonts.TextLayout.IsNeutral(r.Value)) continue;
            hasStrong = true;
            if (font.GetGlyph(r.Value) is > 0) return true;
        }
        return !hasStrong;
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
