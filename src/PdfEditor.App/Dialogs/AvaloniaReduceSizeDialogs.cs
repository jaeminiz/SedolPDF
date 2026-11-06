using Avalonia.Controls;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Dialogs;

/// <summary>IReduceSizeDialogs의 실제 구현. 소유 창 위에 모달로 띄운다. (화면 동작은 검증 필요: 자동 테스트 없음)</summary>
public sealed class AvaloniaReduceSizeDialogs(Window owner) : IReduceSizeDialogs
{
    public async Task<long?> AskTargetBytesAsync(long currentBytes)
        => await new ReduceTargetDialog(currentBytes).ShowDialog<long?>(owner);

    public async Task<NotAchievedChoice> AskNotAchievedAsync(string message, bool rasterAvailable)
    {
        IReadOnlyList<string> buttons = rasterAvailable
            ? [Strings.Btn_ReduceKeep, Strings.Btn_ReduceRasterize, Strings.Btn_ReduceMono, Strings.Dlg_Cancel]
            : [Strings.Btn_ReduceKeep, Strings.Dlg_Cancel];
        var dlg = new MessageDialog(Strings.Dlg_ReduceTitle, message, buttons);
        await dlg.ShowDialog(owner);
        return dlg.Result switch
        {
            0 => NotAchievedChoice.KeepBestEffort,
            1 when rasterAvailable => NotAchievedChoice.Rasterize,
            2 when rasterAvailable => NotAchievedChoice.Monochrome,
            _ => NotAchievedChoice.Cancel,
        };
    }

    public async Task<int?> PickMonochromeLevelAsync(IReadOnlyList<MonoOption> options, long targetBytes)
        => await new MonoQualityDialog(options, targetBytes).ShowDialog<int?>(owner);

    public async Task<bool> ConfirmRasterizeAsync(bool monochrome)
    {
        var dlg = new MessageDialog(Strings.Dlg_ReduceTitle, monochrome ? Strings.Msg_ReduceMonoWarn : Strings.Msg_ReduceRasterWarn, [Strings.Dlg_Ok, Strings.Dlg_Cancel], defaultIndex: 1);
        await dlg.ShowDialog(owner);
        return dlg.Result == 0;
    }
}
