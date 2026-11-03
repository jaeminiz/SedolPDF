using Avalonia.Controls;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;

namespace PdfEditor.App.Printing;

/// <summary>인쇄 명령이 부르는 서비스. 테스트에서는 가짜로 바꾼다.</summary>
public interface IPrintService
{
    /// <param name="selectedPageIndices">썸네일 패널에서 고른 쪽(0부터). 없으면 빈 목록.</param>
    Task PrintAsync(DocumentSession session, int currentPageIndex, IReadOnlyCollection<int> selectedPageIndices);
}

/// <summary>마지막에 쓴 인쇄 옵션을 설정 폴더의 print-options.json에 기억한다. 읽기·쓰기 실패는 조용히 무시한다.</summary>
public sealed class PrintOptionsStore(string filePath)
{
    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath) ?? Path.GetTempPath(), "print-options.json");

    public PrintOptions Load()
    {
        try { return PrintOptionsJson.TryDeserialize(File.Exists(filePath) ? File.ReadAllText(filePath) : null) ?? new PrintOptions(); }
        catch (Exception) { return new PrintOptions(); }
    }

    public void Save(PrintOptions o)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            string tmp = filePath + ".tmp";
            File.WriteAllText(tmp, PrintOptionsJson.Serialize(PrintOptionsJson.ForRemembering(o)));
            File.Move(tmp, filePath, overwrite: true);
        }
        catch (Exception) { /* 기억하지 못해도 인쇄에는 영향 없음 */ }
    }
}

/// <summary>
/// 인쇄 흐름: (출력 사본 준비 + 인쇄 대화상자·미리보기) → 진행 대화상자(취소 가능) → 완료.
/// 화면·실제 프린터 동작은 자동 테스트가 없다(검증 필요: 수동 확인).
/// </summary>
public sealed class AvaloniaPrintService(Window owner, IUserPrompts prompts, IAppearanceSource appearance, IAnnotationPatcher patcher, AnnotationAssets? assets = null,
    IPrinterCatalog? catalog = null, PrintOptionsStore? store = null) : IPrintService
{
    private readonly IPrinterCatalog _catalog = catalog ?? new WinPrinterCatalog();
    private readonly PrintOptionsStore _store = store ?? new PrintOptionsStore(PrintOptionsStore.DefaultPath);

    public async Task PrintAsync(DocumentSession session, int currentPageIndex, IReadOnlyCollection<int> selectedPageIndices)
    {
        if (!session.Permissions.CanPrint) return;

        // 출력 사본(패치 단계를 거친 것)은 대화상자가 열려 있는 동안 백그라운드에서 만든다. 미리보기와 인쇄가 같은 사본을 본다.
        using var prepareCts = new CancellationTokenSource();
        var prepare = PrintSource.PrepareAsync(session, appearance, patcher, assets, prepareCts.Token);
        PrintSource? source = null;
        try
        {
            var sizes = session.Pages.Select(p => (p.WidthPt, p.HeightPt)).ToList();
            var ctx = new PrintDialogContext(sizes, currentPageIndex, selectedPageIndices, _catalog, _store.Load(),
                async (sheet, o, surf, width, ct) =>
                {
                    PrintSource src;
                    try { src = await prepare.WaitAsync(ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { return null; }
                    return await session.Thread.InvokeAsync(() => PrintPreviewRenderer.RenderSheet(src.Document, sheet, o, surf, width, ct), ct);
                });
            var request = await new PrintDialog(ctx).ShowDialog<PrintRequest?>(owner);
            if (request is null) return;
            _store.Save(request.Options);

            try { source = await prepare; }
            catch (Exception) { await prompts.ShowErrorAsync(Strings.Msg_PrintFailed); return; }

            IntPtr dc = PrinterInterop.CreateDc(request.Printer, request.DevMode);
            if (dc == IntPtr.Zero) { await prompts.ShowErrorAsync(Strings.Msg_PrintFailed); return; }

            var sheets = PrintPlan.Build(request.Pages, request.Options.PagesPerSheet, request.SoftwareCopies, request.Options.Collate, request.Options.Duplex != PrintDuplex.Off);
            using var cts = new CancellationTokenSource();
            var progressDlg = new PrintProgressDialog(Math.Max(1, PrintPlan.CountPages(sheets)), cts);
            var progress = new Progress<int>(n => progressDlg.SetProgress(n));
            _ = progressDlg.ShowDialog(owner);
            bool canceled = false;
            try
            {
                // 작업이 끝날 때까지 UI 스레드는 HDC를 건드리지 않는다.
                await PrintJob.RunSheetsAsync(source, dc, sheets, request.Options, progress, cts.Token);
            }
            catch (OperationCanceledException) { canceled = true; }
            catch (Exception)
            {
                // PrintException뿐 아니라 쪽 불러오기 실패(PdfiumException, 손상된 쪽) 등도 같은 길로:
                // 진행 창을 닫고 DC를 돌려준 뒤 알린다. 예외가 새면 진행 창이 남고 DC가 샌다.
                progressDlg.Finish();
                PrintInterop.DeleteDc(dc);
                await prompts.ShowErrorAsync(Strings.Msg_PrintFailed);
                return;
            }
            progressDlg.Finish();
            PrintInterop.DeleteDc(dc);
            if (!canceled) await prompts.ShowInfoAsync(Strings.Msg_PrintDone);
        }
        finally
        {
            prepareCts.Cancel();
            // 사본 만들기가 아직 도는 중이면 끝나기를 기다렸다가 지운다(대화상자를 바로 닫은 경우).
            var toDispose = source ?? await prepare.ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
            if (toDispose is not null) await toDispose.DisposeAsync();
        }
    }
}
