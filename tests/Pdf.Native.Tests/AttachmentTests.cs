using PdfEditor.TestFixtures;

namespace Pdf.Native.Tests;

public class AttachmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public AttachmentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task GetAttachments_NameSizeAndData()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        TestPdfFactory.CreateWithAttachment(P("a.pdf"), "검토 의견.txt", data);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("a.pdf"));
            var list = doc.GetAttachments();
            Assert.Single(list);
            Assert.Equal("검토 의견.txt", list[0].Name);
            Assert.Equal(10, list[0].Size);
            Assert.Equal(data, doc.GetAttachmentData(0));
        });
    }

    [Fact]
    public async Task GetAttachments_None_ReturnsEmpty()
    {
        TestPdfFactory.CreateBlank(P("b.pdf"), 1, 595, 842);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("b.pdf"));
            Assert.Empty(doc.GetAttachments());
        });
    }

    [Theory]
    [InlineData(@"..\..\a.exe", "a.exe")]
    [InlineData(@"C:\x\y.txt", "y.txt")]
    [InlineData("a:b?.txt", "a_b_.txt")]
    [InlineData("CON", "_CON")]
    [InlineData("  ", "attachment")]
    [InlineData("도면/설명.pdf", "설명.pdf")]
    public void SafeFileName_StripsPathAndInvalidChars(string input, string expected)
        => Assert.Equal(expected, SafeFileName.From(input));
}
