using Avalonia.Media;
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

    private AppearanceServices(AppFont? font)
    {
        Font = font;
        Appearance = new PdfAppearanceSource(font);
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
        // 그림 스탬프(Task 10)가 AnnotationAssets에서 비트맵을 꺼내 돌려줄 자리. 그때까지는 비트맵이 없다고 답한다
        // (AppearanceGeometry가 자리 표시 사각형을 그리고 캐시하지 않는다).
        AppearanceGeometry.ImageResolver = static (string _) => (IImage?)null;
        return new AppearanceServices(font);
    }
}
