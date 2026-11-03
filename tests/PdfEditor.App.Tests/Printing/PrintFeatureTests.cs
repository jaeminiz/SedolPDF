using Pdf.Native;
using PdfEditor.App.Printing;
using PdfEditor.Appearance;
using PdfEditor.Document;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Printing;

/// <summary>
/// 새 옵션(모아 찍기·방향·소프트웨어 매수·미리보기 그림)을 Microsoft Print to PDF 파일 출력으로 확인한다.
/// 안전 규칙: 물리 프린터로는 보내지 않는다(PrintJobTests와 같다).
/// </summary>
public class PrintFeatureTests : IAsyncLifetime
{
    private const string PdfPrinter = "Microsoft Print to PDF";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-printfeat-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }
    public Task DisposeAsync() { _t.Dispose(); try { Directory.Delete(_dir, true); } catch { } return Task.CompletedTask; }

    private Task<List<(float W, float H)>> PageSizesAsync(string path) => _t.InvokeAsync(() =>
    {
        using var d = PdfDocument.Open(_t, path);
        var l = new List<(float, float)>();
        for (int i = 0; i < d.PageCount; i++) { using var p = d.LoadPage(i); l.Add((p.SizePt.Width, p.SizePt.Height)); }
        return l;
    });

    /// <summary>요청(= 대화상자 결과와 같은 형태)대로 Print to PDF 파일로 인쇄한다.</summary>
    private async Task PrintAsync(DocumentSession s, PrintOptions o, int[] pages, int softwareCopies, string outFile)
    {
        var cat = new WinPrinterCatalog();
        var caps = cat.QueryCaps(PdfPrinter);
        var dm = cat.DefaultDevMode(PdfPrinter)!;
        var first = s.Pages[pages[0]];
        bool landscape = PrintLayout.IsLandscapeSheet(o.Orientation, o.PagesPerSheet, first.WidthPt, first.HeightPt);
        dm = cat.Apply(PdfPrinter, dm, o with { PaperSize = 9 }, landscape, false, caps);
        IntPtr dc = PrinterInterop.CreateDc(PdfPrinter, dm);
        Assert.NotEqual(IntPtr.Zero, dc);
        try
        {
            await using var src = await PrintSource.PrepareAsync(s, new PdfAppearanceSource(), null, null, CancellationToken.None);
            var sheets = PrintPlan.Build(pages, o.PagesPerSheet, softwareCopies, o.Collate, o.Duplex != PrintDuplex.Off);
            await PrintJob.RunSheetsAsync(src, dc, sheets, o with { PaperSize = 9 }, null, CancellationToken.None, outFile);
        }
        finally { PrintInterop.DeleteDc(dc); }
    }

    [Fact]
    public async Task TwoUp_FiveLabeledPages_ThreeLandscapeSheets()
    {
        string src = Path.Combine(_dir, "s.pdf"), outFile = Path.Combine(_dir, "o.pdf");
        TestPdfFactory.CreateLabeled(src, Enumerable.Repeat((595.28f, 841.89f), 5).ToList());
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        await PrintAsync(s, new PrintOptions(PagesPerSheet: 2), [0, 1, 2, 3, 4], 1, outFile);
        var sizes = await PageSizesAsync(outFile);
        Assert.Equal(3, sizes.Count);
        Assert.All(sizes, z => Assert.True(z.W > z.H, "2쪽 모아 찍기 자동 방향은 가로 용지"));
    }

    [Fact]
    public async Task SoftwareCopies_CollatedAndUncollated_SheetCounts()
    {
        string src = Path.Combine(_dir, "s.pdf");
        TestPdfFactory.CreateBlank(src, 3, 595.28f, 841.89f);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        string a = Path.Combine(_dir, "a.pdf"), b = Path.Combine(_dir, "b.pdf");
        await PrintAsync(s, new PrintOptions(Copies: 2, Collate: true), [0, 1, 2], 2, a);
        await PrintAsync(s, new PrintOptions(Copies: 2, Collate: false), [0, 1, 2], 2, b);
        Assert.Equal(6, (await PageSizesAsync(a)).Count);
        Assert.Equal(6, (await PageSizesAsync(b)).Count);
    }

    [Fact]
    public async Task Orientation_ForcedLandscape_RotatesPortraitPage()
    {
        string src = Path.Combine(_dir, "s.pdf"), outFile = Path.Combine(_dir, "o.pdf");
        TestPdfFactory.CreateBlank(src, 1, 595.28f, 841.89f);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        await PrintAsync(s, new PrintOptions(Orientation: PrintOrientation.Landscape), [0], 1, outFile);
        var z = (await PageSizesAsync(outFile))[0];
        Assert.True(z.W > z.H);
    }

    [Fact]
    public async Task Duplex_OddSetPadsBlankSheet_BetweenSoftwareCopies()
    {
        string src = Path.Combine(_dir, "s.pdf"), outFile = Path.Combine(_dir, "o.pdf");
        TestPdfFactory.CreateBlank(src, 3, 595.28f, 841.89f);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        // 이 프린터(PDF)는 양면이 없어 장치에는 적용되지 않지만, 프로그램의 맞춤 장(빈 장)은 계획대로 들어간다.
        await PrintAsync(s, new PrintOptions(Copies: 2, Duplex: PrintDuplex.LongEdge), [0, 1, 2], 2, outFile);
        Assert.Equal(7, (await PageSizesAsync(outFile)).Count);
    }

    [Fact]
    public async Task PreviewRenderer_PlacesPage_RotatesLandscape_AndGrays()
    {
        string src = Path.Combine(_dir, "s.pdf");
        TestPdfFactory.CreateLabeled(src, [(595.28f, 841.89f), (1000f, 800f)]);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        var surf = new PrintSurface(4960, 7016, 600, 600, 4961, 7016, 0, 0);
        var (portrait, landscape, gray) = await _t.InvokeAsync(() =>
        {
            var p = PrintPreviewRenderer.RenderSheet(s.Document, new PrintSheet([0]), new PrintOptions(), surf, 400);
            var l = PrintPreviewRenderer.RenderSheet(s.Document, new PrintSheet([1]), new PrintOptions(), surf, 400);
            var g = PrintPreviewRenderer.RenderSheet(s.Document, new PrintSheet([0]), new PrintOptions(Grayscale: true), surf, 400);
            return (p, l, g);
        });
        Assert.Equal(400, portrait.Width);
        Assert.InRange(portrait.Height, 565, 566);   // 7016 / 4961 * 400
        Assert.True(HasNonWhite(portrait), "라벨이 그려져 있어야 함");
        Assert.True(HasNonWhite(landscape));
        // 가로 쪽(1000x800)은 세로 용지에 자동 회전하면 폭에 맞춰 위아래에 여백(흰 띠)이 생긴다 — 첫·마지막 줄은 흰색
        Assert.True(RowIsWhite(landscape, 0) && RowIsWhite(landscape, landscape.Height - 1));
        for (int i = 0; i < gray.Bgra.Length; i += 4) Assert.True(gray.Bgra[i] == gray.Bgra[i + 1] && gray.Bgra[i + 1] == gray.Bgra[i + 2]);
    }

    [Fact]
    public void RotateCw_MovesTopLeftToTopRight()
    {
        // 2x1 이미지 [A B] → 시계 방향 90도 → 1x2 [A / B] (A가 위)
        byte[] src = [1, 1, 1, 255, 2, 2, 2, 255];
        var r = PrintPreviewRenderer.RotateCw(src, 2, 1);
        Assert.Equal([1, 1, 1, 255, 2, 2, 2, 255], r);
        // 1x2 [A / B] → 2x1 [B A]
        var r2 = PrintPreviewRenderer.RotateCw(src, 1, 2);
        Assert.Equal([2, 2, 2, 255, 1, 1, 1, 255], r2);
    }

    private static bool HasNonWhite(PreviewBitmap b)
    {
        for (int i = 0; i < b.Bgra.Length; i += 4) if (b.Bgra[i] < 0xE0) return true;
        return false;
    }

    private static bool RowIsWhite(PreviewBitmap b, int y)
    {
        for (int x = 0; x < b.Width; x++) if (b.Bgra[(y * b.Width + x) * 4] != 0xFF) return false;
        return true;
    }
}
