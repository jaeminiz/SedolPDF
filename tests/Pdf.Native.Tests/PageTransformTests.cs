using Pdf.Native.Interop;
using PdfEditor.TestFixtures;

namespace Pdf.Native.Tests;

public class PageTransformTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-pt-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public PageTransformTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PageTransform_MatchesPdfiumPageToDevice(int rotation)
    {
        var path = Path.Combine(_dir, "crop.pdf");
        TestPdfFactory.CreateWithCropBox(path, (100, 100, 400, 500));
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            page.Rotation = rotation;
            const double scale = 1.5;
            var tr = new PageTransform(page.BoundsPt, rotation, scale);
            int w = (int)Math.Round(tr.ViewSize.W), h = (int)Math.Round(tr.ViewSize.H);
            foreach (var (x, y) in new[] { (120.0, 130.0), (390.0, 480.0), (250.0, 300.0) })
            {
                int dx, dy;
                unsafe { Assert.Equal(1, NativeMethods.FPDF_PageToDevice(page.Handle, 0, 0, w, h, 0, x, y, &dx, &dy)); }
                var v = tr.ToView(x, y);
                Assert.InRange(Math.Abs(v.X - dx), 0, 1);
                Assert.InRange(Math.Abs(v.Y - dy), 0, 1);
            }
        });
    }

    [Fact]
    public void ToPage_IsInverseOfToView()
    {
        for (int rot = 0; rot < 4; rot++)
        {
            var tr = new PageTransform(System.Drawing.RectangleF.FromLTRB(100, 100, 400, 500), rot, 1.5);
            foreach (var (x, y) in new[] { (120.0, 130.0), (333.3, 444.4) })
            {
                var v = tr.ToView(x, y);
                var (px, py) = tr.ToPage(v);
                Assert.True(Math.Abs(px - x) < 1e-6 && Math.Abs(py - y) < 1e-6);
            }
        }
    }
}
