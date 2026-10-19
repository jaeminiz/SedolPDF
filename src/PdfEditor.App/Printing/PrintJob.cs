using System.Runtime.InteropServices;
using PdfEditor.Document;
using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Output;
using PdfEditor.Document.Saving;

namespace PdfEditor.App.Printing;

/// <summary>
/// 인쇄 작업 하나. HDC를 만든 스레드와 다른 스레드(PdfiumThread)에서 GDI 인쇄 호출을 하는 것이 설계다
/// (같은 조건의 PrintJobTests로 확인, 결과는 docs/validation/2026-10-m1-results.md).
/// 작업이 끝날 때까지 호출자는 HDC를 건드리지 않는다.
/// </summary>
public sealed class PrintJob
{
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

        var t = s.Thread;
        // 저장 안 한 주석도 인쇄되도록 같은 전용 스레드에서 먼저 PDFium 문서에 반영한다. 실패하면 StartDoc 전에 중단.
        // 패치 계획이 비어 있지 않으면(선·텍스트 상자 등은 편집 중인 문서에 없음) 임시 사본을 만들어 패치하고, 그 사본으로 인쇄한다.
        // 객체 편집(M4)도 편집 중인 문서에는 확정되지 않으므로 같은 조건으로 사본을 쓴다(OutputCopyBuilder).
        string tmp = Path.Combine(Path.GetTempPath(), "~sedol-print-" + Guid.NewGuid().ToString("N") + ".pdf");
        PdfDocument? patched = null;
        try
        {
            var copy = await new OutputCopyBuilder(appearance, patcher, assets).BuildIfNeededAsync(s, tmp, ct).ConfigureAwait(false);
            if (copy is not null)
            {
                string? pw = s.Password;
                patched = await t.InvokeAsync(() => PdfDocument.Open(t, tmp, pw)).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            await PrintPagesAsync(s, patched, hdc, pageIndices, o, progress, ct, outputFile).ConfigureAwait(false);
        }
        finally
        {
            if (patched is not null) { var d = patched; try { await t.InvokeAsync(() => d.Dispose()).ConfigureAwait(false); } catch (Exception) { } }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
        }
    }

    private static async Task PrintPagesAsync(DocumentSession s, PdfDocument? patched, IntPtr hdc, IReadOnlyList<int> pageIndices,
        PrintOptions o, IProgress<int>? progress, CancellationToken ct, string? outputFile)
    {
        var t = s.Thread;
        string docName = s.DisplayName;
        int job = await t.InvokeAsync(() => PrintInterop.StartDoc(hdc, docName, outputFile)).ConfigureAwait(false);
        if (job <= 0) throw new PrintException("StartDoc failed", Marshal.GetLastWin32Error());

        try
        {
            int done = 0;
            foreach (int index in pageIndices)
            {
                ct.ThrowIfCancellationRequested();
                int idx = index;
                await t.InvokeAsync(() => PrintPage(patched ?? s.Document, hdc, idx, o)).ConfigureAwait(false);
                progress?.Report(++done);
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

    private static void PrintPage(PdfDocument doc, IntPtr hdc, int index, PrintOptions o)
    {
        if (!PrintInterop.StartPageOk(hdc))
            throw new PrintException("StartPage failed", Marshal.GetLastWin32Error());
        using (var page = doc.LoadPage(index))
        {
            var size = page.SizePt;
            int w = PrintInterop.Caps(hdc, PrintInterop.HORZRES), h = PrintInterop.Caps(hdc, PrintInterop.VERTRES);
            int dx = PrintInterop.Caps(hdc, PrintInterop.LOGPIXELSX), dy = PrintInterop.Caps(hdc, PrintInterop.LOGPIXELSY);
            var p = PrintLayout.Compute(size.Width, size.Height, w, h, dx, dy, o);
            page.RenderToDc(hdc, p.X, p.Y, p.Width, p.Height, p.Rotate, o.WithAnnotations);
        }
        if (!PrintInterop.EndPageOk(hdc))
            throw new PrintException("EndPage failed", Marshal.GetLastWin32Error());
    }
}
