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
    public void Verify_RasterizedDenseGrid_True()
    {
        // 가는 선이 촘촘한 도면·표: 벡터와 래스터는 안티앨리어싱만 달라서 화소 단위 비교로는 크게 달라 보인다. 같은 모양이면 통과해야 한다.
        string src = P("grid.pdf");
        using (var doc = new PdfSharp.Pdf.PdfDocument())
        {
            var page = doc.AddPage();
            page.Width = new PdfSharp.Drawing.XUnitPt(595);
            page.Height = new PdfSharp.Drawing.XUnitPt(842);
            using (var g = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
            {
                var pen = new PdfSharp.Drawing.XPen(PdfSharp.Drawing.XColors.Black, 0.5);
                for (double y = 40; y < 800; y += 7) g.DrawLine(pen, 40, y, 555, y);
                for (double x = 40; x < 560; x += 31) g.DrawLine(pen, x, 40, x, 800);
            }
            doc.Save(src);
        }
        string ras = P("grid-ras.pdf");
        Rasterizer.Build(_t, src, null, ras, SizeLadder.Raster[^1], CancellationToken.None);
        var c = OutputVerifier.Check(_t, src, ras, null, SizeLadder.Raster[^1].MaxDpi);
        Assert.True(c.Ok, $"mean={c.WorstMean:0.0} max={c.WorstMax:0.0}");
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
