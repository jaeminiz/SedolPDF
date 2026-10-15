using Pdf.Native.Tests.Fixtures;

namespace Pdf.Native.Tests;

public class PdfDocumentTests : IDisposable
{
    private const float A1W = 1683.78f, A1H = 2383.94f;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public PdfDocumentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task CreateSaveReopen_PageCountAndSizeRoundTrip()
    {
        var path = P("a1.pdf");
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.CreateNew(_t);
            doc.AddPage(A1W, A1H);
            doc.AddPage(A1W, A1H);
            doc.SaveCopy(path);
        });
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            Assert.Equal(2, doc.PageCount);
            using var page = doc.LoadPage(1);
            Assert.InRange(page.SizePt.Width, A1W - 0.01f, A1W + 0.01f);
            Assert.InRange(page.SizePt.Height, A1H - 0.01f, A1H + 0.01f);
        });
    }

    [Fact]
    public async Task Open_KoreanPath_Succeeds()
    {
        var path = Path.Combine(_dir, "한글 폴더", "도면 1.pdf");
        TestPdfFactory.CreateBlank(path, 1, 595, 842);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            Assert.Equal(1, doc.PageCount);
        });
    }

    [Fact]
    public async Task Open_Encrypted_WithoutPassword_ThrowsPasswordRequired()
    {
        var path = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(path, 1, 595, 842, "secret");
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() =>
            _t.InvokeAsync(() => PdfDocument.Open(_t, path)));
    }

    [Fact]
    public async Task Open_Encrypted_WithPassword_Succeeds()
    {
        var path = P("enc2.pdf");
        TestPdfFactory.CreateEncrypted(path, 3, 595, 842, "secret");
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path, "secret");
            Assert.Equal(3, doc.PageCount);
        });
    }

    [Fact]
    public async Task Open_GarbageBytes_ThrowsPdfiumExceptionWithFormatCode()
    {
        var path = P("garbage.pdf");
        File.WriteAllBytes(path, Enumerable.Range(0, 2000).Select(i => (byte)(i * 31)).ToArray());
        var ex = await Assert.ThrowsAsync<PdfiumException>(() => _t.InvokeAsync(() => PdfDocument.Open(_t, path)));
        Assert.Equal(3u, ex.ErrorCode);
        Assert.IsNotType<PdfPasswordRequiredException>(ex);
    }

    [Fact]
    public async Task RenderTile_BlankPage_IsAllWhite()
    {
        var path = P("blank.pdf");
        TestPdfFactory.CreateBlank(path, 1, 595, 842);
        var bytes = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            return page.RenderTile(1.0, 0, 0, 256, 256, withAnnotations: true);
        });
        Assert.Equal(256 * 256 * 4, bytes.Length);
        Assert.All(bytes, b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public async Task RenderTile_LargePageHighZoom_AllocatesOnlyTile()
    {
        var path = P("a1b.pdf");
        TestPdfFactory.CreateBlank(path, 1, A1W, A1H);
        var bytes = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            return page.RenderTile(4.0, 1024, 2048, 512, 512, withAnnotations: false);
        });
        Assert.Equal(512 * 512 * 4, bytes.Length);
    }

    [Fact]
    public async Task CallOffThread_Throws()
    {
        var path = P("off.pdf");
        TestPdfFactory.CreateBlank(path, 1, 595, 842);
        var doc = await _t.InvokeAsync(() => PdfDocument.Open(_t, path));
        Assert.Throws<InvalidOperationException>(() => { _ = doc.PageCount; });
        await _t.InvokeAsync(doc.Dispose);
    }
}
