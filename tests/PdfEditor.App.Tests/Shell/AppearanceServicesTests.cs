using PdfEditor.App.Shell;
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
