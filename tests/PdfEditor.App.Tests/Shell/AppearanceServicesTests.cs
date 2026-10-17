using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Viewer.Annotations;

namespace PdfEditor.App.Tests.Shell;

public class AppearanceServicesTests : IDisposable
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

    [Fact]
    public void Create_SetsTextFontAndImageResolver_SharedWithAppearanceSource()
    {
        AppearanceGeometry.TextFont = null;
        AppearanceGeometry.ImageResolver = null;

        var services = AppearanceServices.Create(RepoRootWithFont());

        Assert.NotNull(AppearanceGeometry.TextFont);
        Assert.Same(services.Font, AppearanceGeometry.TextFont);   // 화면·AP·패치가 같은 AppFont 하나
        Assert.NotNull(AppearanceGeometry.ImageResolver);
    }

    [Fact]
    public void Create_DoesNotLoadFontUntilTextIsNeeded()
    {
        var services = AppearanceServices.Create(RepoRootWithFont());
        Assert.False(services.FontLoaded);   // 시작 때 4.6 MB 글꼴을 읽지 않는다 (M3a 성능: 작업 집합 +12~16 MB)

        // 글자 없는 주석(연필)을 만들고 그려도 글꼴은 읽지 않는다
        var ink = new PdfEditor.Document.Annotations.Annotation
        {
            Kind = PdfEditor.Document.Annotations.AnnotKind.Ink, Rect = new Pdf.Native.PdfRectF(10, 10, 60, 60), Color = new PdfEditor.Document.Annotations.PdfColor(200, 0, 0),
            InkStrokes = [[new Pdf.Native.PdfPointF(12, 12), new Pdf.Native.PdfPointF(50, 50)]],
        };
        Assert.True(HeadlessUi.Run(() => AppearanceGeometry.Get(ink, 0).Complete));
        Assert.False(services.FontLoaded);
        Assert.True(services.Appearance.BuildContent(ink, 0).Length > 0);
        Assert.False(services.FontLoaded);

        // 글자가 있는 주석을 그리면 그때 읽고, 화면·AP·패치가 같은 글꼴 하나를 쓴다
        var text = new PdfEditor.Document.Annotations.Annotation
        {
            Kind = PdfEditor.Document.Annotations.AnnotKind.FreeText, Intent = PdfEditor.Document.Annotations.FreeTextIntent.TextBox,
            Rect = new Pdf.Native.PdfRectF(10, 10, 110, 60), Contents = "ABC", FontSizePt = 12f,
        };
        Assert.True(HeadlessUi.Run(() => AppearanceGeometry.Get(text, 0).Complete));
        Assert.True(services.FontLoaded);
        Assert.NotNull(services.Font);
        Assert.Same(services.Font, AppearanceGeometry.TextFont);
    }

    [Fact]
    public void Create_MissingFontFile_LeavesTextFontNullButStillSetsResolver()
    {
        var empty = Path.Combine(Path.GetTempPath(), "pdfeditor-nofont-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var services = AppearanceServices.Create(empty);
            Assert.Null(services.Font);
            Assert.Null(AppearanceGeometry.TextFont);
            Assert.NotNull(AppearanceGeometry.ImageResolver);
        }
        finally { Directory.Delete(empty, true); }
    }
}
