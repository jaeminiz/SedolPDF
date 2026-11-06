using Avalonia.Controls;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Optimize;

namespace PdfEditor.App.Dialogs;

/// <summary>IReduceSizeDialogs의 실제 구현. 소유 창 위에 모달로 띄운다. (화면 동작은 검증 필요: 자동 테스트 없음)</summary>
public sealed class AvaloniaReduceSizeDialogs(Window owner) : IReduceSizeDialogs
{
    public async Task<long?> AskTargetBytesAsync(long currentBytes)
        => await new ReduceTargetDialog(currentBytes).ShowDialog<long?>(owner);

    public async Task<NotAchievedDecision> AskNotAchievedAsync(string message, long bestEffortBytes, long targetBytes, ConversionEstimates? estimates)
    {
        if (estimates is not null)
            return await new NotAchievedDialog(message, bestEffortBytes, targetBytes, estimates).ShowDialog<NotAchievedDecision?>(owner)
                ?? new NotAchievedDecision(NotAchievedKind.Cancel);

        var dlg = new MessageDialog(Strings.Dlg_ReduceTitle, message, [Strings.Btn_ReduceKeep, Strings.Dlg_Cancel]);
        await dlg.ShowDialog(owner);
        return new NotAchievedDecision(dlg.Result == 0 ? NotAchievedKind.KeepBestEffort : NotAchievedKind.Cancel);
    }
}
