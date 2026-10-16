using PdfEditor.App.Documents;
using PdfEditor.App.Settings;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Documents;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

/// <summary>MainWindowViewModel이 읽기 전용 패널 모델을 탭에 붙이고, 패널을 열 때 한 번 읽는지 확인한다.</summary>
public class ReadOnlyPanelWiringTests : PanelTestBase
{
    private static async Task WaitFor(Func<bool> cond, int ms = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            Assert.True(sw.ElapsedMilliseconds < ms, "timeout");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task OpenDocument_AttachesPanels_AndLoadsOnDemand()
    {
        string p = Path.Combine(Dir, "o.pdf");
        TestPdfFactory.CreateWithOutline(p);
        var prompts = new FakePrompts();
        var vm = new MainWindowViewModel();
        vm.AttachWorkflow(new FileWorkflow(Thread, new SaveService([]), new SettingsStore(Path.Combine(Dir, "s.json")), prompts),
            prompts, a => a());

        await vm.OpenAsync(p);

        Assert.False(vm.HasNoDocument);
        Assert.NotNull(vm.ActiveBookmarks);
        Assert.NotNull(vm.ActiveAttachments);
        Assert.NotNull(vm.ActiveLayers);
        Assert.NotNull(vm.ActiveProperties);

        // 속성 패널은 기본으로 보이므로 문서를 열면 읽는다.
        await WaitFor(() => vm.ActiveProperties!.PdfVersion != PdfEditor.App.Resources.Strings.Prop_Unknown);

        // 책갈피 패널을 열면 그때 읽는다.
        Assert.Empty(vm.ActiveBookmarks!.Nodes);
        vm.SetPanelCommand.Execute(PanelKind.Bookmarks);
        await WaitFor(() => vm.ActiveBookmarks!.Nodes.Count == 2);
        Assert.True(vm.ShowBookmarks);
        Assert.False(vm.ShowThumbnails);
    }
}
