using Avalonia.Input;
using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Viewer.Objects;
using PdfEditor.TextEdit.Tests;

namespace PdfEditor.App.Tests.Shell;

/// <summary>0.4.0 Task 6: 찾아 바꾸기가 창에 어떻게 이어져 있는가 — 명령·Ctrl+H·리본·패널 단추·검색 패널의 "찾아 바꾸기…".</summary>
[Collection("TabSettings")]
public class FindReplaceWiringTests : ObjectUiTestBase
{
    [Fact]
    public void Command_HasCtrlH_AndIcon_AndIsOnTheHomeTab()
    {
        var vm = new MainWindowViewModel();
        var cmd = vm.Commands.Find("find.replace");
        Assert.NotNull(cmd);
        Assert.Equal("findreplace", cmd!.IconKey);
        Assert.Equal(Key.H, cmd.Gesture!.Key);
        Assert.Equal(KeyModifiers.Control, cmd.Gesture.KeyModifiers);
        Assert.Equal(Strings.Cmd_FindReplace, cmd.Label);
        foreach (var tab in new[] { "Home" })
            Assert.Contains(vm.Ribbon[tab].SelectMany(g => g.Items), i => i.CommandId == "find.replace");
        Assert.Contains(vm.PanelButtons, p => p.Kind == PanelKind.Replace && p.IconKey == "findreplace");
        // 다른 명령과 단축키가 겹치지 않는다
        var dup = vm.Commands.All.Where(c => c.Gesture is not null).GroupBy(c => (c.Gesture!.Key, c.Gesture.KeyModifiers)).Where(g => g.Count() > 1).ToList();
        Assert.Empty(dup);
    }

    [Fact]
    public async Task Command_OpensTheReplacePanel_AndAsksForFocus()
    {
        var s = await OpenAsync(p => DrawingIdPdfFactory.Create(p, IdCase.Whole));
        var vm = new MainWindowViewModel();
        vm.ShowSession(s);
        vm.ActiveLeftPanel = PanelKind.Thumbnails;
        vm.IsLeftPanelVisible = false;
        int focus = 0;
        vm.FocusReplaceRequested += (_, _) => focus++;

        vm.Commands.Find("find.replace")!.Command.Execute(null);

        Assert.Equal(PanelKind.Replace, vm.ActiveLeftPanel);
        Assert.True(vm.IsLeftPanelVisible);
        Assert.True(vm.ShowReplace);
        Assert.NotNull(vm.ActiveReplace);
        Assert.Equal(1, focus);
        Assert.Equal(340, vm.LeftPanelWidth);
        Assert.Equal(Strings.Panel_Replace, vm.ActivePanelLabel);

        // Esc(패널 안)로 닫기 요청 → 왼쪽 패널이 접힌다
        vm.ActiveReplace!.RequestClose();
        Assert.False(vm.IsLeftPanelVisible);
    }

    [Fact]
    public async Task SearchPanel_OpenReplace_CarriesTheQuery()
    {
        var s = await OpenAsync(p => DrawingIdPdfFactory.Create(p, IdCase.Whole));
        var vm = new MainWindowViewModel();
        vm.ShowSession(s);
        vm.ActiveDocument!.Search!.Query = "H-2501";

        vm.ActiveDocument.Search.OpenReplaceCommand.Execute(null);

        Assert.Equal(PanelKind.Replace, vm.ActiveLeftPanel);
        Assert.Equal("H-2501", vm.ActiveReplace!.FindText);
    }

    [Fact]
    public async Task ClosingTheTab_ReleasesTheFindResult()
    {
        var s = await OpenAsync(p => DrawingIdPdfFactory.Create(p, IdCase.Whole));
        var vm = new MainWindowViewModel();
        vm.ShowSession(s);
        var rep = vm.ActiveReplace!;
        rep.FindText = "H-2501"; rep.ReplaceText = "H-1052";
        await rep.FindCommand.ExecuteAsync(null);
        Assert.NotEmpty(rep.Rows);
        rep.Dispose();   // 탭을 닫을 때(Detached) 불리는 길
        Assert.Empty(rep.Rows);
        Assert.False(rep.IsBusy);
    }
}
