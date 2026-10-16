using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.Document;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

public class LayersViewModelTests : PanelTestBase
{
    [Fact]
    public async Task Layers_ListOnly_ShowsNamesAndNote()
    {
        string p = Path.Combine(Dir, "l.pdf");
        TestPdfFactory.CreateWithLayers(p, ["배관", "전기"]);
        var vm = new LayersViewModel(await OpenTab(p));

        await vm.LoadAsync();

        Assert.Equal(["배관", "전기"], vm.Items.Select(i => i.Name));
        Assert.All(vm.Items, i => Assert.True(i.IsVisibleInitially));
        Assert.Equal(Strings.Panel_LayerToggleUnavailable, vm.ToggleNote);
        Assert.True(vm.HasLayers);
    }

    [Fact]
    public async Task Layers_None_ShowsEmptyMessage()
    {
        string p = Path.Combine(Dir, "b.pdf");
        TestPdfFactory.CreateBlank(p, 1, 595, 842);
        var vm = new LayersViewModel(await OpenTab(p));
        await vm.LoadAsync();
        Assert.False(vm.HasLayers);
        Assert.Equal(Strings.Panel_NoLayers, vm.Message);
    }

    [Fact]
    public async Task Layers_NewDocumentWithoutFile_ShowsNoLayers()
    {
        var s = await DocumentSession.CreateNewAsync(Thread);
        try
        {
            var tab = new DocumentTabViewModel(s, "new", null);
            var vm = new LayersViewModel(tab);
            await vm.LoadAsync();
            Assert.Equal(Strings.Panel_NoLayers, vm.Message);
        }
        finally { await s.DisposeAsync(); }
    }

    [Fact]
    public async Task Layers_UnreadableFile_ShowsUnreadableMessage()
    {
        string p = Path.Combine(Dir, "l.pdf");
        TestPdfFactory.CreateWithLayers(p, ["a"]);
        var tab = await OpenTab(p);
        File.WriteAllBytes(p, [1, 2, 3]);   // 연 뒤에 파일이 깨졌다
        var vm = new LayersViewModel(tab);
        await vm.LoadAsync();
        Assert.Equal(Strings.Panel_LayersUnreadable, vm.Message);
        Assert.False(vm.HasLayers);
    }
}
