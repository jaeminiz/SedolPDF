using PdfEditor.App.Settings;
using PdfEditor.App.Shell;
using Xunit;

namespace PdfEditor.App.Tests.Shell;

public class PanelWidthTests
{
    [Fact]
    public void Defaults_match_the_old_fixed_widths()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal(220, vm.PropertiesPanelWidth);
        vm.ActiveLeftPanel = PanelKind.Comments; Assert.Equal(320, vm.LeftPanelWidth);
        vm.ActiveLeftPanel = PanelKind.Replace; Assert.Equal(340, vm.LeftPanelWidth);
        vm.ActiveLeftPanel = PanelKind.Thumbnails; Assert.Equal(200, vm.LeftPanelWidth);
    }

    [Fact]
    public void Left_width_is_clamped_remembered_per_kind_and_reset_to_default()
    {
        var vm = new MainWindowViewModel { ActiveLeftPanel = PanelKind.Thumbnails };
        vm.SetLeftPanelWidth(10); Assert.Equal(MainWindowViewModel.LeftPanelMinWidth, vm.LeftPanelWidth);
        vm.SetLeftPanelWidth(5000); Assert.Equal(MainWindowViewModel.LeftPanelMaxWidth, vm.LeftPanelWidth);
        vm.SetLeftPanelWidth(260);
        vm.ActiveLeftPanel = PanelKind.Comments; Assert.Equal(320, vm.LeftPanelWidth);   // 다른 패널은 영향 없음
        vm.ActiveLeftPanel = PanelKind.Thumbnails; Assert.Equal(260, vm.LeftPanelWidth);
        vm.ResetLeftPanelWidth(); Assert.Equal(200, vm.LeftPanelWidth);
        Assert.Empty(vm.LeftPanelWidths);
    }

    [Fact]
    public void Properties_width_is_clamped_and_reset()
    {
        var vm = new MainWindowViewModel();
        vm.SetPropertiesPanelWidth(50); Assert.Equal(MainWindowViewModel.PropertiesPanelMinWidth, vm.PropertiesPanelWidth);
        vm.SetPropertiesPanelWidth(9999); Assert.Equal(MainWindowViewModel.PropertiesPanelMaxWidth, vm.PropertiesPanelWidth);
        vm.SetPropertiesPanelWidth(300); Assert.Equal(300, vm.SavedPropertiesPanelWidth);
        vm.ResetPropertiesPanelWidth(); Assert.Equal(220, vm.PropertiesPanelWidth); Assert.Null(vm.SavedPropertiesPanelWidth);
    }

    [Fact]
    public void Width_change_raises_event_and_survives_settings_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), "panelw-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(path);
            var vm = new MainWindowViewModel { ActiveLeftPanel = PanelKind.Comments };
            int n = 0;
            vm.PanelWidthsChanged += (_, _) =>
            {
                n++;
                store.Update(s => s with { LeftPanelWidths = vm.LeftPanelWidths.ToDictionary(k => k.Key.ToString(), k => k.Value), PropertiesPanelWidth = vm.SavedPropertiesPanelWidth });
            };
            vm.SetLeftPanelWidth(400); vm.SetPropertiesPanelWidth(300);
            Assert.Equal(2, n);

            var vm2 = new MainWindowViewModel();
            vm2.ApplyPanelWidths(new SettingsStore(path).Load());
            vm2.ActiveLeftPanel = PanelKind.Comments;
            Assert.Equal(400, vm2.LeftPanelWidth);
            Assert.Equal(300, vm2.PropertiesPanelWidth);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Corrupt_saved_widths_are_ignored_or_clamped()
    {
        var vm = new MainWindowViewModel();
        vm.ApplyPanelWidths(AppSettings.Default with
        {
            LeftPanelWidths = new Dictionary<string, double> { ["Nope"] = 300, ["Comments"] = 99999, ["Layers"] = double.NaN },
            PropertiesPanelWidth = -5,
        });
        vm.ActiveLeftPanel = PanelKind.Comments; Assert.Equal(MainWindowViewModel.LeftPanelMaxWidth, vm.LeftPanelWidth);
        vm.ActiveLeftPanel = PanelKind.Layers; Assert.Equal(200, vm.LeftPanelWidth);
        Assert.Equal(MainWindowViewModel.PropertiesPanelMinWidth, vm.PropertiesPanelWidth);
    }
}
