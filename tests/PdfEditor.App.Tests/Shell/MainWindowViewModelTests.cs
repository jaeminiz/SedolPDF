using PdfEditor.App.Documents;
using PdfEditor.App.Shell;
using PdfEditor.App.Shell.Ribbon;

namespace PdfEditor.App.Tests.Shell;

public class MainWindowViewModelTests
{
    [Fact]
    public void ViewModel_Defaults()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal("Home", vm.SelectedRibbonTab);
        Assert.Equal(PanelKind.Thumbnails, vm.ActiveLeftPanel);
        Assert.True(vm.IsPropertiesPanelVisible);
        Assert.True(vm.IsLeftPanelVisible);
        Assert.False(vm.IsFullScreen);
        Assert.Empty(vm.Documents);
        Assert.Null(vm.ActiveDocument);
        Assert.Empty(vm.QuickCommandResults);
    }

    [Fact]
    public void ViewModel_AllRibbonCommandsRegistered()
    {
        var vm = new MainWindowViewModel();
        var ids = vm.Ribbon.Values.SelectMany(gs => gs).SelectMany(g => g.Items).Select(i => i.CommandId)
            .Concat(vm.FileMenuCommandIds).Concat(vm.QuickAccessCommandIds).Distinct().ToList();
        Assert.NotEmpty(ids);
        var missing = ids.Where(id => vm.Commands.Find(id) is null).ToList();
        Assert.True(missing.Count == 0, "Not registered: " + string.Join(", ", missing));
        Assert.Equal(["Home", "Annotate", "Edit", "Page", "View"], vm.Ribbon.Keys);
    }

    [Fact]
    public void ViewModel_RibbonTabSelection_ChangesGroups()
    {
        var vm = new MainWindowViewModel();
        var home = vm.CurrentRibbonGroups;
        vm.SelectedRibbonTab = "Page";
        Assert.NotSame(home, vm.CurrentRibbonGroups);
        Assert.True(vm.RibbonTabs.Single(t => t.Id == "Page").IsSelected);
        Assert.False(vm.RibbonTabs.Single(t => t.Id == "Home").IsSelected);
    }

    [Fact]
    public void ViewModel_AnnotateAndEdit_AreDisabledWithoutDocument_EditHasTooltip()
    {
        var vm = new MainWindowViewModel();
        foreach (var tab in new[] { "Annotate", "Edit" })
            foreach (var g in vm.Ribbon[tab].SelectMany(x => x.Items).Where(i => i.Tool is null))
            {
                Assert.False(vm.Commands.Find(g.CommandId)!.Command.CanExecute(null), g.CommandId);
                if (tab == "Edit") Assert.False(string.IsNullOrEmpty(g.DisabledTip), g.CommandId);
            }
        foreach (var g in vm.Ribbon["Annotate"].SelectMany(x => x.Items))
            Assert.False(vm.Commands.Find(g.CommandId)!.Command.CanExecute(null), g.CommandId);
        // M3 주석 명령은 등록만 유지: 빠른 실행에서는 비활성 + 안내
        var m3 = vm.Commands.Find("annot.line")!;
        Assert.False(m3.Command.CanExecute(null));
        Assert.False(string.IsNullOrEmpty(m3.DisabledTip));
    }

    [Fact]
    public void AnnotateTab_TwelveWorkingTools_InGroups()
    {
        var vm = new MainWindowViewModel();
        var groups = vm.Ribbon["Annotate"];
        Assert.Equal(4, groups.Count);
        var ids = groups.SelectMany(g => g.Items).Select(i => i.CommandId).ToList();
        Assert.Equal(["tool.hand", "tool.textSelect", "annot.select", "annot.sticky", "annot.hlText", "annot.hlArea",
                      "annot.underline", "annot.strike", "annot.rect", "annot.ellipse", "annot.pencil", "annot.eraser"], ids);
        Assert.True(ids.Count <= 15);
        Assert.Equal([false, false, true, true], groups.Select(g => g.Compact));
    }

    [Fact]
    public void HomeTab_HasAnnotateGroup()
    {
        var vm = new MainWindowViewModel();
        var g = vm.Ribbon["Home"].Single(x => x.Name == PdfEditor.App.Resources.Strings.Group_Annotate);
        Assert.True(g.Compact);
        Assert.Equal(["annot.hlText", "annot.sticky", "annot.pencil"], g.Items.Select(i => i.CommandId));
    }

    [Fact]
    public void ToolButton_IsChecked_FollowsActiveTool()
    {
        var vm = new MainWindowViewModel();
        var d = new DocumentTabViewModel("a.pdf");
        vm.Documents.Add(d);
        vm.ActiveDocument = d;
        vm.SelectedRibbonTab = "Annotate";
        var buttons = vm.CurrentRibbonGroups.SelectMany(g => g.Items).ToList();
        Assert.Equal(12, buttons.Count);
        Assert.Equal([ToolKind.Hand], buttons.Where(b => b.IsChecked).Select(b => b.Tool!.Value));
        d.Tool = ToolKind.Pencil;
        Assert.Equal([ToolKind.Pencil], buttons.Where(b => b.IsChecked).Select(b => b.Tool!.Value));
        d.Tool = ToolKind.Eraser;
        Assert.Equal([ToolKind.Eraser], buttons.Where(b => b.IsChecked).Select(b => b.Tool!.Value));
        vm.ActiveDocument = null;
        Assert.DoesNotContain(buttons, b => b.IsChecked);
    }

    [Fact]
    public void ViewModel_PanelToggles_AndFullScreen()
    {
        var vm = new MainWindowViewModel();
        vm.Commands.Find("view.leftPanel")!.Command.Execute(null);
        Assert.False(vm.IsLeftPanelVisible);
        vm.Commands.Find("view.properties")!.Command.Execute(null);
        Assert.False(vm.IsPropertiesPanelVisible);
        vm.Commands.Find("view.fullscreen")!.Command.Execute(null);
        Assert.True(vm.IsFullScreen);
        vm.SetPanelCommand.Execute(PanelKind.Search);
        Assert.Equal(PanelKind.Search, vm.ActiveLeftPanel);
        Assert.True(vm.IsLeftPanelVisible);
    }

    [Fact]
    public void ViewModel_QuickCommand_SearchesRegistry()
    {
        var vm = new MainWindowViewModel();
        vm.QuickCommandText = "페이지";
        Assert.NotEmpty(vm.QuickCommandResults);
        vm.QuickCommandText = "";
        Assert.Empty(vm.QuickCommandResults);
    }

    [Fact]
    public void ViewModel_ZoomSlider_LogScale_10To1600()
    {
        var vm = new MainWindowViewModel();
        vm.ZoomSliderValue = 0; Assert.Equal(10, vm.ZoomPercent, 6);
        vm.ZoomSliderValue = 1; Assert.Equal(1600, vm.ZoomPercent, 6);
        vm.ZoomPercent = 100; Assert.Equal(Math.Log(10) / Math.Log(160), vm.ZoomSliderValue, 6);
    }

    [Fact]
    public void ViewModel_ActiveDocument_DrivesStatus()
    {
        var vm = new MainWindowViewModel();
        var d = new DocumentTabViewModel("a.pdf") { PageCount = 12, CurrentPageIndex = 2, PageWidthMm = 420, PageHeightMm = 297 };
        vm.Documents.Add(d);
        vm.ActiveDocument = d;
        Assert.Equal(3, vm.CurrentPageNumber);
        Assert.Equal(12, vm.PageCount);
        Assert.Contains("420.0", vm.StatusSizeText);
        vm.CloseDocumentCommand.Execute(d);
        Assert.Empty(vm.Documents);
        Assert.Null(vm.ActiveDocument);
    }
}
