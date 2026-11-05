using Pdf.Native;
using PdfEditor.TestFixtures;

namespace PdfEditor.Optimize.Tests;

public sealed class OutputVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-verify-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public OutputVerifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    [Fact]
    public void Verify_SameFile_True()
    {
        string a = P("a.pdf");
        ImagePdfFactory.Create(a, new ImageSpec(400, 300, ImageKind.FlateRgb, 300, 200));
        Assert.True(OutputVerifier.Verify(_t, a, a, null));
    }

    [Fact]
    public void Verify_DifferentPageCount_False()
    {
        string a = P("a2.pdf"), b = P("b2.pdf");
        TestPdfFactory.CreateBlank(a, 2, 200, 200);
        TestPdfFactory.CreateBlank(b, 3, 200, 200);
        Assert.False(OutputVerifier.Verify(_t, a, b, null));
    }

    [Fact]
    public void Verify_VisuallyDifferentPage_False()
    {
        string blank = P("blank3.pdf"), picture = P("pic3.pdf");
        TestPdfFactory.CreateBlank(blank, 1, 612, 792);
        // 쪽 대부분을 덮는 검은에 가까운 이미지: 평균 밝기가 크게 다르다
        ImagePdfFactory.Create(picture, new ImageSpec(300, 300, ImageKind.FlateGray, 612, 792));
        Assert.False(OutputVerifier.Verify(_t, blank, picture, null));
    }
}
