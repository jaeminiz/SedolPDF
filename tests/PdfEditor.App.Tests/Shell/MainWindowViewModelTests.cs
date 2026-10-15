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
    public void ViewModel_AnnotateAndEdit_AreDisabledWithTooltip()
    {
        var vm = new MainWindowViewModel();
        foreach (var tab in new[] { "Annotate", "Edit" })
            foreach (var g in vm.Ribbon[tab].SelectMany(x => x.Items))
            {
                Assert.False(vm.Commands.Find(g.CommandId)!.Command.CanExecute(null), g.CommandId);
                Assert.False(string.IsNullOrEmpty(g.DisabledTip), g.CommandId);
            }
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
