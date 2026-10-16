using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Xunit.Abstractions;

namespace Pdf.Native.Tests;

/// <summary>V9/V2 탐침: M2 저장 방식(D1, PDFium만으로 AP 쓰기)의 전제를 확인한다.</summary>
public class V9AnnotWriteProbeTests : IDisposable
{
    private const string ApContent = "q /GS gs 1 0 0 RG 2 w 101 601 198 48 re S Q";
    private readonly ITestOutputHelper _out;
    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-v9-" + Guid.NewGuid().ToString("N"));

    public V9AnnotWriteProbeTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private Task<string> MakeWithAp(string name) => _t.InvokeAsync(() =>
    {
        var path = P(name);
        using var doc = Pdf.Native.PdfDocument.CreateNew(_t);
        doc.AddPage(595, 842);
        using var page = doc.LoadPage(0);
        using var a = page.CreateAnnotation(PdfAnnotSubtype.Square);
        a.Rect = new PdfRectF(100, 600, 300, 650);
        a.SetColor(PdfColorType.Color, 255, 0, 0, 0.5f);
        a.SetAppearance(ApContent);
        doc.SaveCopy(path);
        return path;
    });

    private static PdfDictionary Deref(PdfItem item) => item is PdfReference r ? (PdfDictionary)r.Value : (PdfDictionary)item;

    [Fact]
    public async Task V9_SetAp_WithOpacity_AddsExtGStateGs()
    {
        var path = await MakeWithAp("v9.pdf");
        using var d = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var annot = Deref(d.Pages[0].Elements.GetArray("/Annots")!.Elements[0]);
        var n = annot.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!;
        var bbox = n.Elements.GetArray("/BBox")!;
        var bb = new[] { 0, 1, 2, 3 }.Select(i => bbox.Elements.GetReal(i)).ToArray();
        _out.WriteLine($"BBox = [{string.Join(' ', bb)}], Matrix = {(n.Elements.ContainsKey("/Matrix") ? n.Elements["/Matrix"]?.ToString() : "없음")}");
        Assert.Equal([100.0, 600.0, 300.0, 650.0], bb);

        if (n.Elements.ContainsKey("/Matrix"))
        {
            var m = n.Elements.GetArray("/Matrix")!;
            Assert.Equal([1.0, 0.0, 0.0, 1.0, 0.0, 0.0], new[] { 0, 1, 2, 3, 4, 5 }.Select(i => m.Elements.GetReal(i)).ToArray());
        }

        var gs = n.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/ExtGState")!.Elements.GetDictionary("/GS")!;
        double ca = gs.Elements.GetReal("/CA"), lower = gs.Elements.GetReal("/ca");
        _out.WriteLine($"ExtGState /GS: CA={ca}, ca={lower}");
        Assert.InRange(ca, 0.49, 0.51);
        Assert.InRange(lower, 0.49, 0.51);
    }

    [Fact]
    public async Task V9_SetAp_ContentIsInPageCoordinates()
    {
        var path = await MakeWithAp("v9r.pdf");
        var buf = await _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            return page.RenderTile(1.0, 0, 0, 595, 842, new RenderOptions(true));
        });
        // 위쪽 선: PDF y=649 -> 화면 y=193 근처, x=200 (선 두께 2라 192~193)
        int i = (193 * 595 + 200) * 4;
        byte b = buf[i], g = buf[i + 1], r = buf[i + 2];
        _out.WriteLine($"선 위 픽셀 RGB=({r},{g},{b})");
        Assert.True(r > 200 && g < 200 && b < 200 && r - g > 50, $"붉은 계열이어야 함: ({r},{g},{b})");
        foreach (var (x, y) in new[] { (50, 50), (200, 100), (200, 300), (550, 193) })
        {
            int j = (y * 595 + x) * 4;
            Assert.True(buf[j] == 255 && buf[j + 1] == 255 && buf[j + 2] == 255, $"({x},{y})는 흰색이어야 함");
        }
    }

    [Fact]
    public async Task V9_OurAp_SurvivesRenderAndSave()
    {
        var path = await MakeWithAp("v9s.pdf");
        var path2 = P("v9s2.pdf");
        var (ap1, ap2) = await _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            page.RenderTile(1.0, 0, 0, 595, 842, new RenderOptions(true));
            string? first;
            using (var a = page.GetAnnotation(0)) first = a.GetAppearance();
            doc.SaveCopy(path2);
            using var doc2 = Pdf.Native.PdfDocument.Open(_t, path2);
            using var page2 = doc2.LoadPage(0);
            using var b = page2.GetAnnotation(0);
            return (first, b.GetAppearance());
        });
        Assert.Equal(ApContent, ap1);
        Assert.Equal(ApContent, ap2);
    }

    [Fact]
    public async Task V2_NoApAnnot_Probe()
    {
        var path = P("v2.pdf");
        var path2 = P("v2b.pdf");
        await _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.CreateNew(_t);
            doc.AddPage(595, 842);
            using var page = doc.LoadPage(0);
            using (var a = page.CreateAnnotation(PdfAnnotSubtype.Square))
            {
                a.Rect = new PdfRectF(100, 600, 300, 650);
                a.SetColor(PdfColorType.Color, 255, 0, 0, 1f);
                a.SetBorderWidth(3);
            }
            var tile = page.RenderTile(1.0, 0, 0, 595, 842, new RenderOptions(true));
            doc.SaveCopy(path);
            using var doc2 = Pdf.Native.PdfDocument.Open(_t, path);
            using var p2 = doc2.LoadPage(0);
            var tile2 = p2.RenderTile(1.0, 0, 0, 595, 842, new RenderOptions(true));
            doc2.SaveCopy(path2);
            int red = 0, red2 = 0;
            for (int i = 0; i < tile.Length; i += 4) if (tile[i + 2] > 180 && tile[i + 1] < 90) red++;
            for (int i = 0; i < tile2.Length; i += 4) if (tile2[i + 2] > 180 && tile2[i + 1] < 90) red2++;
            _out.WriteLine($"AP 없는 Square 렌더: 만든 직후 빨강 픽셀 {red}, 저장 후 다시 연 뒤 {red2}");
            return 0;
        });
        foreach (var p in new[] { path, path2 })
        {
            using var d = PdfReader.Open(p, PdfDocumentOpenMode.Import);
            var annot = Deref(d.Pages[0].Elements.GetArray("/Annots")!.Elements[0]);
            bool hasAp = annot.Elements.ContainsKey("/AP");
            _out.WriteLine($"{Path.GetFileName(p)}: /AP {(hasAp ? "있음" : "없음")}, 키 = {string.Join(' ', annot.Elements.Keys)}");
        }
    }
}
