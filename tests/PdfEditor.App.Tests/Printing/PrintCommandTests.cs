using PdfEditor.App.Documents;
using PdfEditor.App.Printing;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Documents;
using PdfEditor.App.Tests.Panels;
using PdfEditor.Document;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Printing;

/// <summary>인쇄 명령 활성/비활성과 서비스 호출 (실제 인쇄는 하지 않는 가짜 서비스).</summary>
public class PrintCommandTests : PanelTestBase
{
    private sealed class FakePrint : IPrintService
    {
        public int Calls;
        public int LastPage = -1;
        public Task PrintAsync(DocumentSession session, int currentPageIndex) { Calls++; LastPage = currentPageIndex; return Task.CompletedTask; }
    }

    private async Task<(MainWindowViewModel Vm, FakePrint Print)> OpenAsync(string path)
    {
        var prompts = new FakePrompts();
        var vm = new MainWindowViewModel();
        vm.AttachWorkflow(new FileWorkflow(Thread, new SaveService([]), new SettingsStore(Path.Combine(Dir, "s.json")), prompts), prompts, a => a());
        var print = new FakePrint();
        vm.AttachPrinting(print);
        await vm.OpenAsync(path);
        return (vm, print);
    }

    [Fact]
    public async Task PrintCommand_Enabled_AndCallsService()
    {
        string p = Path.Combine(Dir, "a.pdf");
        TestPdfFactory.CreateBlank(p, 2, 595.28f, 841.89f);
        var (vm, print) = await OpenAsync(p);
        var cmd = vm.Commands.Find("file.print")!.Command;
        Assert.True(cmd.CanExecute(null));
        Assert.Null(vm.PrintDisabledTip);
        cmd.Execute(null);
        await Task.Delay(100);
        Assert.Equal(1, print.Calls);
    }

    [Fact]
    public async Task PrintCommand_PrintForbidden_DisabledWithTip()
    {
        string p = Path.Combine(Dir, "r.pdf");
        TestPdfFactory.CreateOwnerRestricted(p, allowPrint: false, allowModify: true, allowCopy: true);
        var (vm, print) = await OpenAsync(p);
        Assert.False(vm.Commands.Find("file.print")!.Command.CanExecute(null));
        Assert.Equal(Strings.Msg_PrintRestricted, vm.PrintDisabledTip);
        Assert.Equal(0, print.Calls);
    }

    [Fact]
    public void PrintCommand_NoDocument_Disabled()
    {
        var vm = new MainWindowViewModel();
        vm.AttachPrinting(new FakePrint());
        Assert.False(vm.Commands.Find("file.print")!.Command.CanExecute(null));
    }
}
