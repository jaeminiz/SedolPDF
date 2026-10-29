using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Tests.I18n;

/// <summary>영어 화면에서도 한글 PDF 글(주석 글자)은 앱 글꼴(Noto Sans KR)로 그대로 그려지고 저장된다 (0.4.0 Task 10).</summary>
public class EnglishKoreanTextTests : IDisposable
{
    public void Dispose()
    {
        AppearanceGeometry.TextFont = null;
        AppearanceGeometry.ImageResolver = null;
    }

    private static string RepoRootWithFont()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("assets/fonts/NotoSansKR-Regular.otf");
    }

    private static Annotation KoreanBox(string text) => new()
    {
        Kind = AnnotKind.FreeText, Intent = FreeTextIntent.TextBox,
        Rect = new Pdf.Native.PdfRectF(10, 10, 200, 60), Contents = text, FontSizePt = 12f,
    };

    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    public void KoreanAnnotationText_UsesAppFont_InEitherUiLanguage(string lang)
    {
        var (shapes, content) = HeadlessUi.Run(() =>
        {
            using var _ = new CultureScope(lang);
            var services = AppearanceServices.Create(RepoRootWithFont());
            var a = KoreanBox("한글 도면 호선 H-2501");
            var cached = AppearanceGeometry.Get(a, 0);
            Assert.True(cached.Complete);
            Assert.True(services.FontLoaded);
            return (cached.Texts.Count, services.Appearance.BuildContent(a, 0).Length);
        });
        Assert.True(shapes > 0);     // 글자 윤곽선이 만들어졌다(글꼴이 한글을 가졌다)
        Assert.True(content > 0);    // 저장용 AP 내용도 만들어졌다
    }

    [Fact]
    public void AppFontLookup_DoesNotDependOnUiLanguage()
    {
        // 글꼴 파일은 실행 폴더의 assets\fonts에서 찾는다 — 언어와 무관하다.
        int ko = 0, en = 0;
        HeadlessUi.Run(() =>
        {
            using (new CultureScope("ko")) ko = AppearanceServices.Create(RepoRootWithFont()).Font is null ? 0 : 1;
            using (new CultureScope("en")) en = AppearanceServices.Create(RepoRootWithFont()).Font is null ? 0 : 1;
            return 0;
        });
        Assert.Equal(1, ko);
        Assert.Equal(ko, en);
    }
}
