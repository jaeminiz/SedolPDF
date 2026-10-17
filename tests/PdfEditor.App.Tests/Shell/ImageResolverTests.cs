using Avalonia.Media.Imaging;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Shell;

/// <summary>화면용 그림 스탬프 비트맵: 폭 상한, id별 캐시, 동시 호출, 보관소 검증 (M3a Task 10 fix).</summary>
public class ImageResolverTests : IDisposable
{
    public void Dispose()
    {
        AppearanceGeometry.TextFont = null;
        AppearanceGeometry.ImageResolver = null;
        AppearanceGeometry.Assets = AnnotationAssets.Empty;
    }

    private static AppearanceServices Create()
    {
        var empty = Path.Combine(Path.GetTempPath(), "pdfeditor-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try { return AppearanceServices.Create(empty); }
        finally { Directory.Delete(empty, true); }
    }

    private static byte[] Strip(int w, int h) => TestImages.Png(w, h, (x, _) => ((byte)(x % 256), (byte)0, (byte)0, (byte)255));

    [Fact]
    public void WideImage_DecodedToBoundedWidth_SmallImageKeepsSize()
    {
        var services = Create();
        string big = services.Assets.AddImage(Strip(3000, 4));
        string small = services.Assets.AddImage(Strip(40, 4));
        var resolve = AppearanceGeometry.ImageResolver!;
        var (bw, sw) = HeadlessUi.Run(() => (resolve(big)!.Size.Width, resolve(small)!.Size.Width));
        Assert.Equal(AppearanceServices.MaxDecodeWidth, bw);
        Assert.Equal(40, sw);
    }

    [Fact]
    public void SameId_SameBitmap_AndConcurrentCallsAgree_UnknownIdIsNull()
    {
        var services = Create();
        string id = services.Assets.AddImage(Strip(50, 5));
        var resolve = AppearanceGeometry.ImageResolver!;
        var results = HeadlessUi.Run(() => Enumerable.Range(0, 8).AsParallel().Select(_ => resolve(id)).ToArray());
        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Null(resolve("ffffffffffffffff"));
    }

    [Fact]
    public void SharedAssets_RejectOversizedImages()
    {
        var services = Create();
        Assert.Throws<ArgumentException>(() => services.Assets.AddImage(TestImages.PngHeaderOnly(10000, 10000)));
        Assert.Same(services.Assets, AppearanceGeometry.Assets);
    }
}
