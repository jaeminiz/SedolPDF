using Avalonia.Controls;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Printing;

/// <summary>인쇄 명령이 부르는 서비스. 테스트에서는 가짜로 바꾼다.</summary>
public interface IPrintService
{
    Task PrintAsync(DocumentSession session, int currentPageIndex);
}

/// <summary>
/// 인쇄 흐름: 옵션 대화상자 → Win32 인쇄 대화상자 → 진행 대화상자(취소 가능) → 완료.
/// 화면·실제 프린터 동작은 자동 테스트가 없다(검증 필요: 수동 확인).
/// </summary>
public sealed class AvaloniaPrintService(Window owner, IUserPrompts prompts, IAppearanceSource appearance) : IPrintService
{
    public async Task PrintAsync(DocumentSession session, int currentPageIndex)
    {
        if (!session.Permissions.CanPrint) return;

        var options = await new PrintOptionsDialog().ShowDialog<PrintOptions?>(owner);
        if (options is null) return;

        IntPtr hwnd = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        PrintDialogResult? chosen;
        try { chosen = PrintInterop.ShowPrintDialog(hwnd, session.Pages.Length, currentPageIndex); }
        catch (PrintException) { await prompts.ShowErrorAsync(Strings.Msg_PrintFailed); return; }
        if (chosen is null || chosen.PageIndices.Count == 0)
        {
            if (chosen is not null) PrintInterop.DeleteDc(chosen.Dc);
            return;
        }

        using var cts = new CancellationTokenSource();
        var progressDlg = new PrintProgressDialog(chosen.PageIndices.Count, cts);
        var progress = new Progress<int>(n => progressDlg.SetProgress(n));
        _ = progressDlg.ShowDialog(owner);
        bool canceled = false;
        try
        {
            // 작업이 끝날 때까지 UI 스레드는 HDC를 건드리지 않는다.
            await PrintJob.RunAsync(session, chosen.Dc, chosen.PageIndices, options, progress, cts.Token, appearance);
        }
        catch (OperationCanceledException) { canceled = true; }
        catch (Exception)
        {
            // PrintException뿐 아니라 쪽 불러오기 실패(PdfiumException, 손상된 쪽) 등도 같은 길로:
            // 진행 창을 닫고 DC를 돌려준 뒤 알린다. 예외가 새면 진행 창이 남고 DC가 샌다.
            progressDlg.Finish();
            PrintInterop.DeleteDc(chosen.Dc);
            await prompts.ShowErrorAsync(Strings.Msg_PrintFailed);
            return;
        }
        progressDlg.Finish();
        PrintInterop.DeleteDc(chosen.Dc);
        if (!canceled) await prompts.ShowInfoAsync(Strings.Msg_PrintDone);
    }
}
