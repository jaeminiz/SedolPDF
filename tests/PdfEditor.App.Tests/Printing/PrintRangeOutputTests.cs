using Pdf.Native;
using PdfEditor.App.Printing;
using PdfEditor.Appearance;
using PdfEditor.Document;
using PdfSharp.Drawing;

namespace PdfEditor.App.Tests.Printing;

/// <summary>
/// 쪽 범위·홀짝·매수(한 부씩 / 아님)가 실제 출력 파일에서 "맞는 쪽이 맞는 순서로" 나오는지 Microsoft Print to PDF로 확인한다(0.5.0 마무리 검토).
/// 쪽 k(1부터)에는 폭 60·k pt인 검은 막대가 있어, 출력 쪽의 막대 폭으로 어느 쪽인지 알아낸다. 물리 프린터로는 보내지 않는다.
/// </summary>
public class PrintRangeOutputTests : IAsyncLifetime
{
    private const string PdfPrinter = "Microsoft Print to PDF";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-printrange-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }
    public Task DisposeAsync() { _t.Dispose(); try { Directory.Delete(_dir, true); } catch { } return Task.CompletedTask; }

    private static void CreateBars(string path, int pages)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            var p = doc.AddPage();
            p.Width = new XUnitPt(595.28);
            p.Height = new XUnitPt(841.89);
            using var gfx = XGraphics.FromPdfPage(p);
            gfx.DrawRectangle(XBrushes.Black, new XRect(100, 300, 60 * (i + 1), 40));
        }
        doc.Save(path);
    }

    /// <summary>출력 쪽마다 가장 긴 검은 가로 줄의 길이로 원래 쪽 번호(1부터)를 알아낸다.</summary>
    private Task<List<int>> IdentifyAsync(string path) => _t.InvokeAsync(() =>
    {
        using var d = PdfDocument.Open(_t, path);
        var ids = new List<int>();
        for (int i = 0; i < d.PageCount; i++)
        {
            using var p = d.LoadPage(i);
            const double scale = 0.5;
            int w = (int)(p.SizePt.Width * scale), h = (int)(p.SizePt.Height * scale);
            var px = p.RenderTile(scale, 0, 0, w, h, true);
            int best = 0;
            for (int y = 0; y < h; y++)
            {
                int run = 0;
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    run = px[o] < 64 && px[o + 1] < 64 && px[o + 2] < 64 ? run + 1 : 0;
                    best = Math.Max(best, run);
                }
            }
            ids.Add((int)Math.Round(best / scale / 60.0));
        }
        return ids;
    });

    private async Task PrintAsync(DocumentSession s, PrintOptions o, string outFile)
    {
        Assert.True(PrintPageSelection.TryResolve(o, s.Pages.Length, 0, [], out var pages));
        var cat = new WinPrinterCatalog();
        var caps = cat.QueryCaps(PdfPrinter);
        var dm = cat.Apply(PdfPrinter, cat.DefaultDevMode(PdfPrinter)!, o with { PaperSize = 9 }, false, false, caps);
        IntPtr dc = PrinterInterop.CreateDc(PdfPrinter, dm);
        Assert.NotEqual(IntPtr.Zero, dc);
        try
        {
            await using var src = await PrintSource.PrepareAsync(s, new PdfAppearanceSource(), null, null, CancellationToken.None);
            var sheets = PrintPlan.Build(pages, o.PagesPerSheet, o.Copies, o.Collate, false);
            await PrintJob.RunSheetsAsync(src, dc, sheets, o with { PaperSize = 9 }, null, CancellationToken.None, outFile);
        }
        finally { PrintInterop.DeleteDc(dc); }
    }

    [Theory]
    [InlineData("5,2-3", PrintParity.All, 1, true, new[] { 5, 2, 3 })]
    [InlineData("1-6", PrintParity.Odd, 1, true, new[] { 1, 3, 5 })]
    [InlineData("1-6", PrintParity.Even, 1, true, new[] { 2, 4, 6 })]
    [InlineData("5,2", PrintParity.All, 2, true, new[] { 5, 2, 5, 2 })]
    [InlineData("5,2", PrintParity.All, 2, false, new[] { 5, 5, 2, 2 })]
    public async Task CustomRange_Parity_Copies_PrintRightPagesInOrder(string range, PrintParity parity, int copies, bool collate, int[] expected)
    {
        string src = Path.Combine(_dir, "s.pdf"), outFile = Path.Combine(_dir, "o.pdf");
        CreateBars(src, 6);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        await PrintAsync(s, new PrintOptions(Range: PrintRangeMode.Custom, RangeText: range, Parity: parity, Copies: copies, Collate: collate,
            Scaling: PrintScaling.ActualSize), outFile);
        Assert.Equal(expected, await IdentifyAsync(outFile));
    }
}
