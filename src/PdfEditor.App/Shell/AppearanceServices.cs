using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PdfEditor.Document.Annotations;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Appearance;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;

namespace PdfEditor.App.Shell;

/// <summary>
/// 앱 시작 때 한 번 만드는 주석 모양 부품. Noto Sans KR <see cref="AppFont"/> 하나를 AP 만들기(PdfAppearanceSource),
/// 패치(PdfSharpAnnotationPatcher), 화면(AppearanceGeometry.TextFont)이 함께 쓴다. 글꼴 파일이 없으면 Font가 null이고
/// 글자가 필요한 주석(FreeText)을 만들거나 저장할 때 예외가 난다.
/// </summary>
public sealed class AppearanceServices
{
    public AppFont? Font { get; }
    public PdfAppearanceSource Appearance { get; }
    public PdfSharpAnnotationPatcher Patcher { get; }

    /// <summary>그림 스탬프 이미지·첨부 파일 보관소. 모양 만들기(가로세로 비율), 화면(비트맵), 저장·복구·인쇄(패치)가 모두 이 하나를 쓴다.</summary>
    public AnnotationAssets Assets { get; }

    private AppearanceServices(AppFont? font, AnnotationAssets assets)
    {
        Font = font;
        Assets = assets;
        Appearance = new PdfAppearanceSource(font, assets);
        Patcher = font is null ? new PdfSharpAnnotationPatcher(() => []) : new PdfSharpAnnotationPatcher(font);
    }

    /// <summary>baseDirectory/assets/fonts/NotoSansKR-Regular.otf를 읽어 만들고 AppearanceGeometry의 정적 설정을 채운다.</summary>
    public static AppearanceServices Create(string baseDirectory)
    {
        AppFont? font = null;
        try
        {
            var data = AppFontFile.ReadOrEmpty(baseDirectory);
            if (data.Length > 0) font = AppFont.FromBytes(data);
        }
        catch (Exception) { /* 글꼴을 못 읽어도 앱은 뜬다: 글자가 필요한 주석에서 알린다 */ }

        AppearanceGeometry.TextFont = font;
        // 그림 스탬프: AnnotationAssets의 PNG·JPEG를 Avalonia 비트맵으로 풀어 id별로 캐시한다(스레드 안전).
        // 보관소에 없거나 풀 수 없으면 null: AppearanceGeometry가 자리 표시 사각형을 그리고 캐시하지 않는다.
        var assets = new AnnotationAssets();
        AppearanceGeometry.Assets = assets;
        AppearanceGeometry.ImageResolver = new ImageCache(assets).Resolve;
        return new AppearanceServices(font, assets);
    }

    /// <summary>이미지 id → 비트맵. 같은 id는 한 번만 풀고, 실패는 캐시하지 않는다.</summary>
    internal sealed class ImageCache(AnnotationAssets assets)
    {
        private readonly ConcurrentDictionary<string, Bitmap> _bitmaps = new(StringComparer.Ordinal);

        public IImage? Resolve(string id)
        {
            if (_bitmaps.TryGetValue(id, out var cached)) return cached;
            if (!assets.HasImage(id)) return null;
            try
            {
                using var ms = new MemoryStream(assets.GetImage(id).Data, writable: false);
                return _bitmaps.GetOrAdd(id, new Bitmap(ms));
            }
            catch (Exception) { return null; }
        }
    }
}
