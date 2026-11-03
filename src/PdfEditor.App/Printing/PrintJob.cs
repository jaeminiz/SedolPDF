using System.Runtime.InteropServices;
using PdfEditor.Document;
using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Output;

namespace PdfEditor.App.Printing;

/// <summary>
/// 인쇄·미리보기가 함께 쓰는 출력 사본(설계서 4.2-6). 저장 안 한 주석·객체 편집이 있으면 임시 사본을 만들어 열고, 없으면 편집 중인 문서를 그대로 쓴다.
/// 사본 만들기는 대화상자를 여는 동안 백그라운드에서 한 번만 하고, 미리보기와 인쇄가 같은 사본을 본다. 다 쓰면 DisposeAsync로 사본을 닫고 지운다.
/// </summary>
public sealed class PrintSource : IAsyncDisposable
{
    private readonly DocumentSession _s;
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "~sedol-print-" + Guid.NewGuid().ToString("N") + ".pdf");
    private PdfDocument? _patched;

    private PrintSource(DocumentSession s) { _s = s; }

    /// <summary>PDFium 전용 스레드 안에서만 쓴다.</summary>
    public PdfDocument Document => _patched ?? _s.Document;

    public DocumentSession Session => _s;

    /// <summary>실패하면(주석 반영 실패 등) 예외를 던지고, 만들던 사본은 지운다.</summary>
    public static async Task<PrintSource> PrepareAsync(DocumentSession s, IAppearanceSource appearance, IAnnotationPatcher? patcher, AnnotationAssets? assets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(appearance);
        var src = new PrintSource(s);
        try
        {
            var t = s.Thread;
            // 저장 안 한 주석도 인쇄되도록 같은 전용 스레드에서 먼저 PDFium 문서에 반영한다. 패치 계획이 비어 있지 않거나
            // 객체 편집(M4)이 있으면 임시 사본을 만들어 패치하고 그 사본을 쓴다(OutputCopyBuilder).
            var copy = await new OutputCopyBuilder(appearance, patcher, assets).BuildIfNeededAsync(s, src._tmp, ct).ConfigureAwait(false);
            if (copy is not null)
            {
                string? pw = s.Password;
                string tmp = src._tmp;
                src._patched = await t.InvokeAsync(() => PdfDocument.Open(t, tmp, pw)).ConfigureAwait(false);
            }
            return src;
        }
        catch
        {
            await src.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var d = Interlocked.Exchange(ref _patched, null);
        if (d is not null) { try { await _s.Thread.InvokeAsync(() => d.Dispose()).ConfigureAwait(false); } catch (Exception) { } }
        try { if (File.Exists(_tmp)) File.Delete(_tmp); } catch (Exception) { }
    }
}

/// <summary>
/// 인쇄 작업 하나. HDC를 만든 스레드와 다른 스레드(PdfiumThread)에서 GDI 인쇄 호출을 하는 것이 설계다
/// (같은 조건의 PrintJobTests로 확인, 결과는 docs/validation/2026-10-m1-results.md).
/// 작업이 끝날 때까지 호출자는 HDC를 건드리지 않는다.
/// </summary>
public sealed class PrintJob
{
    /// <summary>쪽 목록을 한 쪽에 한 장씩 인쇄한다(0.4.0 호환 진입점). 사본을 직접 만들고 끝나면 지운다.</summary>
    /// <param name="outputFile">지정하면 프린터가 그 파일에 직접 쓴다(Microsoft Print to PDF 등, 테스트·검증용). 보통 null.</param>
    public static async Task RunAsync(DocumentSession s, IntPtr hdc, IReadOnlyList<int> pageIndices, PrintOptions o,
        IProgress<int>? progress, CancellationToken ct, IAppearanceSource appearance, IAnnotationPatcher? patcher, string? outputFile = null,
        AnnotationAssets? assets = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(appearance);
        foreach (int i in pageIndices)
            if (i < 0 || i >= s.Pages.Length) throw new ArgumentOutOfRangeException(nameof(pageIndices));
        ct.ThrowIfCancellationRequested();

        await using var src = await PrintSource.PrepareAsync(s, appearance, patcher, assets, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var sheets = PrintPlan.Build(pageIndices, o.PagesPerSheet, 1, true, false);
        await RunSheetsAsync(src, hdc, sheets, o, progress, ct, outputFile).ConfigureAwait(false);
    }

    /// <summary>준비된 출력 사본으로 용지(장) 목록을 인쇄한다. progress는 그린 쪽 수(칸 수).</summary>
    public static async Task RunSheetsAsync(PrintSource src, IntPtr hdc, IReadOnlyList<PrintSheet> sheets, PrintOptions o,
        IProgress<int>? progress, CancellationToken ct, string? outputFile = null)
    {
        ArgumentNullException.ThrowIfNull(src);
        var s = src.Session;
        var t = s.Thread;
        string docName = s.DisplayName;
        int job = await t.InvokeAsync(() => PrintInterop.StartDoc(hdc, docName, outputFile)).ConfigureAwait(false);
        if (job <= 0) throw new PrintException("StartDoc failed", Marshal.GetLastWin32Error());

        try
        {
            int done = 0;
            foreach (var sheet in sheets)
            {
                ct.ThrowIfCancellationRequested();
                var sh = sheet;
                await t.InvokeAsync(() => PrintSheetPage(src.Document, hdc, sh, o)).ConfigureAwait(false);
                done += sheet.Cells.Count(c => c.HasValue);
                progress?.Report(done);
            }
            ct.ThrowIfCancellationRequested();
            bool ok = await t.InvokeAsync(() => PrintInterop.EndDocOk(hdc)).ConfigureAwait(false);
            if (!ok) throw new PrintException("EndDoc failed", Marshal.GetLastWin32Error());
        }
        catch
        {
            await t.InvokeAsync(() => PrintInterop.AbortDocQuiet(hdc)).ConfigureAwait(false);
            throw;
        }
    }

    private static void PrintSheetPage(PdfDocument doc, IntPtr hdc, PrintSheet sheet, PrintOptions o)
    {
        if (!PrintInterop.StartPageOk(hdc))
            throw new PrintException("StartPage failed", Marshal.GetLastWin32Error());
        int w = PrintInterop.Caps(hdc, PrintInterop.HORZRES), h = PrintInterop.Caps(hdc, PrintInterop.VERTRES);
        int dx = PrintInterop.Caps(hdc, PrintInterop.LOGPIXELSX), dy = PrintInterop.Caps(hdc, PrintInterop.LOGPIXELSY);
        int n = sheet.Cells.Length;
        for (int c = 0; c < n; c++)
        {
            if (sheet.Cells[c] is not int index) continue;   // 빈 칸(양면 맞춤 빈 장 등)
            var cell = PrintLayout.Cell(c, n, w > h, w, h);
            using var page = doc.LoadPage(index);
            var size = page.SizePt;
            var p = PrintLayout.Compute(size.Width, size.Height, cell.W, cell.H, dx, dy, o with { PagesPerSheet = n });
            page.RenderToDc(hdc, cell.X + p.X, cell.Y + p.Y, p.Width, p.Height, p.Rotate, o.WithAnnotations, o.Grayscale);
        }
        if (!PrintInterop.EndPageOk(hdc))
            throw new PrintException("EndPage failed", Marshal.GetLastWin32Error());
    }
}
