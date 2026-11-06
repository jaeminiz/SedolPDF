using Pdf.Native;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfEditor.Optimize.Tests;

public sealed class Jbig2EncoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-jbig2-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public Jbig2EncoderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>선·상자·점잡음이 섞인 도면 비슷한 1비트 그림(1 = 검정).</summary>
    internal static byte[] Drawing(int w, int h)
    {
        var px = new byte[w * h];
        void Set(int x, int y) { if (x >= 0 && y >= 0 && x < w && y < h) px[y * w + x] = 1; }
        for (int x = 10; x < w - 10; x++) { Set(x, 10); Set(x, 11); Set(x, h - 12); Set(x, h - 11); }
        for (int y = 10; y < h - 10; y++) { Set(10, y); Set(11, y); Set(w - 12, y); Set(w - 11, y); }
        for (int i = 0; i < Math.Min(w, h) - 30; i++) Set(15 + i, 15 + i);
        for (int i = 0; i < w - 40; i++) Set(20 + i, 40 + (int)(10 * Math.Sin(i / 9.0)));
        var rnd = new Random(7);
        for (int i = 0; i < w * h / 60; i++) px[rnd.Next(w * h)] = 1;
        return px;
    }

    private string WritePdf(byte[] jbig2, int w, int h)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Width = new XUnitPt(w);
        page.Height = new XUnitPt(h);
        var img = new PdfDictionary(doc);
        img.Elements["/Type"] = new PdfName("/XObject");
        img.Elements["/Subtype"] = new PdfName("/Image");
        img.Elements["/Width"] = new PdfInteger(w);
        img.Elements["/Height"] = new PdfInteger(h);
        img.Elements["/BitsPerComponent"] = new PdfInteger(1);
        img.Elements["/ColorSpace"] = new PdfName("/DeviceGray");
        img.Elements["/Filter"] = new PdfName("/JBIG2Decode");
        img.CreateStream(jbig2);
        doc.Internals.AddObject(img);
        var xo = new PdfDictionary(doc);
        xo.Elements["/Im0"] = img.Reference!;
        page.Resources.Elements["/XObject"] = xo;
        page.Contents.AppendContent().CreateStream(System.Text.Encoding.ASCII.GetBytes($"q {w} 0 0 {h} 0 0 cm /Im0 Do Q\n"));
        string path = Path.Combine(_dir, "j.pdf");
        doc.Save(path);
        return path;
    }

    [Theory]
    [InlineData(300, 200)]
    [InlineData(257, 131)]   // 8의 배수가 아닌 크기
    public async Task Encode_RenderedByPdfium_MatchesSource(int w, int h)
    {
        byte[] px = Drawing(w, h);
        string path = WritePdf(Jbig2Encoder.EncodePdfStream(px, w, h), w, h);
        byte[] bgra = await _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(_t, path, null);
            using var page = doc.LoadPage(0);
            return page.RenderTile(1.0, 0, 0, w, h, false);
        });
        int diff = 0;
        for (int i = 0; i < w * h; i++)
        {
            bool black = bgra[i * 4] < 128;
            if (black != (px[i] == 1)) diff++;
        }
        Assert.True(diff <= w * h / 1000, $"{diff} of {w * h} pixels differ");
    }

    [Fact]
    public void Encode_LineDrawing_IsMuchSmallerThanRaw()
    {
        byte[] px = Drawing(1200, 800);
        byte[] enc = Jbig2Encoder.EncodePdfStream(px, 1200, 800);
        Assert.True(enc.Length < 1200 * 800 / 8 / 4, $"{enc.Length} bytes");
    }
}
