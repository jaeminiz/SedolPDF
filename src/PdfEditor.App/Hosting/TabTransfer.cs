using Avalonia;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Shell;

namespace PdfEditor.App.Hosting;

/// <summary>
/// 탭을 새 창·다른 창으로 옮긴다(0.8.0 여러 창 설계 5). 세션은 닫지 않고 그대로 넘기며, 옛 창의 탭 모델은 버리고 새 창이 새로 만든다.
/// 저장·용량 줄이기가 진행 중인 문서는 옮기지 않고 알린다. 부르기 전에 창이 입력 중인 글을 확정해 둔다(MainWindow.CommitPendingInputAsync).
/// </summary>
public sealed class TabTransfer(AppHost host)
{
    /// <summary>탭을 새 창으로 옮긴다. 창의 탭이 하나뿐이면(옮겨도 같은 모양) 아무 일도 하지 않고 false.</summary>
    public bool MoveToNewWindow(MainWindow from, DocumentTabViewModel tab, PixelPoint? at = null)
    {
        if (from.ViewModel.Documents.Count <= 1 || !from.ViewModel.Documents.Contains(tab) || Refused(tab)) return false;
        if (from.ViewModel.DetachForTransfer(tab) is not { } packet) return false;
        var w = host.CreateWindow(from, at);
        w.ViewModel.AcceptTransfer(packet, 0);
        return true;
    }

    /// <summary>
    /// 탭을 다른 창의 slot 자리로 옮기고 그 창을 앞으로 가져온다. 같은 창이면 순서만 바꾼다.
    /// 옮긴 뒤 원래 창에 탭이 없으면 그 창을 닫는다(묻지 않음 — 닫을 문서가 없다).
    /// </summary>
    public bool MoveToWindow(MainWindow from, DocumentTabViewModel tab, MainWindow to, int slot)
    {
        if (ReferenceEquals(from, to)) return from.ViewModel.MoveDocument(tab, slot);
        if (to.IsClosed || !from.ViewModel.Documents.Contains(tab) || Refused(tab)) return false;
        if (from.ViewModel.DetachForTransfer(tab) is not { } packet) return false;
        to.ViewModel.AcceptTransfer(packet, slot);
        to.BringToFront();
        if (from.ViewModel.Documents.Count == 0) from.Close();
        return true;
    }

    private bool Refused(DocumentTabViewModel tab)
    {
        if (tab.Session is not { } s || !host.Workflow.IsBusy(s)) return false;
        _ = host.Prompts.ShowInfoAsync(Strings.Msg_TabBusyCannotMove);
        return true;
    }
}
