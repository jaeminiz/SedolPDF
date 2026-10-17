using PdfEditor.Appearance.Fonts;

namespace PdfEditor.Appearance.Tests.Fonts;

public class AppFontFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-fontfile-" + Guid.NewGuid().ToString("N"));

    public AppFontFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Missing_ReturnsEmpty_ThenFoundAfterCopy_AndCached()
    {
        var provider = AppFontFile.CachedProvider(_dir);
        Assert.Empty(provider());   // 없으면 빈 배열(캐시 안 함)

        Directory.CreateDirectory(Path.GetDirectoryName(AppFontFile.PathIn(_dir))!);
        File.WriteAllBytes(AppFontFile.PathIn(_dir), [1, 2, 3]);
        var first = provider();
        Assert.Equal(new byte[] { 1, 2, 3 }, first);
        File.Delete(AppFontFile.PathIn(_dir));
        Assert.Same(first, provider());   // 찾은 뒤에는 캐시
    }

    [Fact]
    public void TestOutput_HasFontBesideAssets()
    {
        // 저장소의 실제 글꼴이 읽히는지(앱 출력 폴더 복사는 App csproj가 한다)
        Assert.True(File.Exists(FontTestSupport.FontPath()));
    }
}
