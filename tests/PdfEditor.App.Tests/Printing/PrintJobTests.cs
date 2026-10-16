using Pdf.Native;
using PdfEditor.App.Printing;
using PdfEditor.Appearance;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Printing;

/// <summary>
/// 안전 규칙: 물리 프린터로는 절대 보내지 않는다. 모든 출력은 "Microsoft Print to PDF" + 임시 파일 경로(DOCINFO 출력 파일)로만 한다.
/// </summary>
public class PrintJobTests : IAsyncLifetime
{
    private const string PdfPrinter = "Microsoft Print to PDF";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-print-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }

    public Task DisposeAsync()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
        return Task.CompletedTask;
    }

    private sealed class SyncProgress(Action<int> a) : IProgress<int> { public void Report(int v) => a(v); }

    /// <summary>테스트 스레드(= PDFium 스레드가 아님)에서 DC를 만든다. 없으면 실패.</summary>
    private static IntPtr CreatePdfPrinterDc()
    {
        IntPtr dc = PrintInterop.CreateDc(PdfPrinter);
        if (dc == IntPtr.Zero) Assert.Fail("Microsoft Print to PDF가 설치되어 있지 않음");
        return dc;
    }

    private Task<int> PageCountOfAsync(string path) => _t.InvokeAsync(() =>
    {
        using var d = PdfDocument.Open(_t, path);
        return d.PageCount;
    });

    private Task<bool> HasNonWhiteAsync(string path) => _t.InvokeAsync(() =>
    {
        using var d = PdfDocument.Open(_t, path);
        using var p = d.LoadPage(0);
        var sz = p.SizePt;
        int w = (int)sz.Width, h = (int)sz.Height;
        var px = p.RenderTile(1.0, 0, 0, w, h, true);
        for (int i = 0; i < px.Length; i += 4)
            if (px[i] < 0xF0 || px[i + 1] < 0xF0 || px[i + 2] < 0xF0) return true;
        return false;
    });

    [Fact]
    public async Task PrintJob_MicrosoftPrintToPdf_WritesAllSelectedPages()
    {
        string src = Path.Combine(_dir, "src.pdf");
        string outFile = Path.Combine(_dir, "out.pdf");
        TestPdfFactory.CreateLabeled(src, [(595.28f, 841.89f), (595.28f, 841.89f), (595.28f, 841.89f)]);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        IntPtr dc = CreatePdfPrinterDc();
        try
        {
            var reported = new List<int>();
            await PrintJob.RunAsync(s, dc, [0, 2], new PrintOptions(), new SyncProgress(reported.Add), CancellationToken.None, new PdfAppearanceSource(), outFile);
            Assert.Equal([1, 2], reported);
        }
        finally { PrintInterop.DeleteDc(dc); }

        Assert.True(File.Exists(outFile));
        Assert.Equal(2, await PageCountOfAsync(outFile));
    }

    [Fact]
    public async Task PrintJob_Cancel_AbortsDoc()
    {
        string src = Path.Combine(_dir, "src.pdf");
        string outFile = Path.Combine(_dir, "cancel.pdf");
        TestPdfFactory.CreateBlank(src, 3, 595.28f, 841.89f);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        IntPtr dc = CreatePdfPrinterDc();
        using var cts = new CancellationTokenSource();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PrintJob.RunAsync(s, dc, [0, 1, 2], new PrintOptions(), new SyncProgress(n => { if (n == 1) cts.Cancel(); }), cts.Token, new PdfAppearanceSource(), outFile));
        }
        finally { PrintInterop.DeleteDc(dc); }

        // 중단된 작업은 완성된 3쪽 PDF를 남기지 않는다.
        if (File.Exists(outFile) && new FileInfo(outFile).Length > 0)
        {
            int pages;
            try { pages = await PageCountOfAsync(outFile); } catch { pages = 0; }
            Assert.True(pages < 3, $"중단했는데 {pages}쪽 PDF가 만들어짐");
        }
    }

    [Fact]
    public async Task PrintJob_WithoutAnnotations_StickyNoteNotPrinted()
    {
        string src = Path.Combine(_dir, "note.pdf");
        TestPdfFactory.CreateBlank(src, 1, 595.28f, 841.89f);
        TestPdfFactory.AddTextAnnotation(src, 0, printFlag: true);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);

        string with = Path.Combine(_dir, "with.pdf");
        string without = Path.Combine(_dir, "without.pdf");
        foreach (var (file, annots) in new[] { (with, true), (without, false) })
        {
            IntPtr dc = CreatePdfPrinterDc();
            try { await PrintJob.RunAsync(s, dc, [0], new PrintOptions(WithAnnotations: annots), null, CancellationToken.None, new PdfAppearanceSource(), file); }
            finally { PrintInterop.DeleteDc(dc); }
        }

        Assert.True(await HasNonWhiteAsync(with), "주석 포함 인쇄인데 주석이 보이지 않음 (테스트 전제 확인)");
        Assert.False(await HasNonWhiteAsync(without), "주석 제외 인쇄인데 흰 페이지가 아님");
    }

    private Task<bool> HasRedAsync(string path) => _t.InvokeAsync(() =>
    {
        using var d = PdfDocument.Open(_t, path);
        using var p = d.LoadPage(0);
        var sz = p.SizePt;
        var px = p.RenderTile(1.0, 0, 0, (int)sz.Width, (int)sz.Height, true);
        for (int i = 0; i < px.Length; i += 4)
            if (px[i + 2] > 0xC8 && px[i + 1] < 0x70 && px[i] < 0x70) return true;
        return false;
    });

    [Fact]
    public async Task Print_UnsavedAnnotation_AppearsInOutput()
    {
        string src = Path.Combine(_dir, "plain.pdf");
        string outFile = Path.Combine(_dir, "printed.pdf");
        TestPdfFactory.CreateBlank(src, 1, 595.28f, 841.89f);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        await s.AnnotationsReady;
        var sq = new Annotation
        {
            Id = s.Annotations.NewId(),
            Kind = AnnotKind.Square,
            Rect = new PdfRectF(100, 500, 300, 700),
            Color = new PdfColor(229, 55, 45),
            FillColor = new PdfColor(229, 55, 45),
            BorderWidth = 2f,
            Name = Guid.NewGuid().ToString("D"),
            Flags = PdfAnnotFlags.Print,
        };
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, sq));
        Assert.True(s.IsDirty);

        IntPtr dc = CreatePdfPrinterDc();
        try { await PrintJob.RunAsync(s, dc, [0], new PrintOptions(), null, CancellationToken.None, new PdfAppearanceSource(), outFile); }
        finally { PrintInterop.DeleteDc(dc); }

        Assert.True(s.IsDirty);   // 인쇄는 저장이 아니다
        Assert.True(await HasRedAsync(outFile), "저장 안 한 빨강 Square가 인쇄 결과에 없음");
    }
}
