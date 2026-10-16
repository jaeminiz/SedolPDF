using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.App.Tests.Documents;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

public class AttachmentsViewModelTests : PanelTestBase
{
    [Fact]
    public async Task Attachments_Save_WritesSanitizedName()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        string p = Path.Combine(Dir, "a.pdf");
        TestPdfFactory.CreateWithAttachment(p, "..\\evil.txt", data);
        var prompts = new FakePrompts();
        string target = Path.Combine(Dir, "saved.bin");
        prompts.SavePicks.Enqueue(target);
        var vm = new AttachmentsViewModel(await OpenTab(p), prompts);

        await vm.LoadAsync();
        var item = Assert.Single(vm.Items);
        await vm.SaveAsync(item);

        Assert.Equal(["evil.txt"], prompts.SuggestedNames);
        Assert.Equal(data, File.ReadAllBytes(target));
        Assert.Empty(prompts.Errors);
    }

    [Fact]
    public async Task Attachments_Save_Cancelled_WritesNothing()
    {
        string p = Path.Combine(Dir, "a.pdf");
        TestPdfFactory.CreateWithAttachment(p, "a.txt", [1, 2, 3]);
        var prompts = new FakePrompts();   // 저장 위치 선택이 null(취소)
        var vm = new AttachmentsViewModel(await OpenTab(p), prompts);
        await vm.LoadAsync();

        await vm.SaveAsync(vm.Items[0]);

        Assert.Equal(1, prompts.SavePickCalls);
        Assert.Single(Directory.GetFiles(Dir));   // a.pdf 뿐
    }

    [Fact]
    public async Task Attachments_ListShowsNameAndSizeKb()
    {
        string p = Path.Combine(Dir, "a.pdf");
        TestPdfFactory.CreateWithAttachment(p, "검토 의견.txt", new byte[2048]);
        var vm = new AttachmentsViewModel(await OpenTab(p), new FakePrompts());
        await vm.LoadAsync();

        var item = Assert.Single(vm.Items);
        Assert.Equal("검토 의견.txt", item.Name);
        Assert.Equal(string.Format(Strings.Panel_AttachmentSize, 2.0), item.SizeText);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task Attachments_None_ShowsEmptyMessage()
    {
        string p = Path.Combine(Dir, "b.pdf");
        TestPdfFactory.CreateBlank(p, 1, 595, 842);
        var vm = new AttachmentsViewModel(await OpenTab(p), new FakePrompts());
        await vm.LoadAsync();
        Assert.True(vm.IsEmpty);
        Assert.Equal(Strings.Panel_NoAttachments, vm.EmptyText);
    }

    [Fact]
    public async Task Attachments_Save_WriteFails_ShowsError()
    {
        string p = Path.Combine(Dir, "a.pdf");
        TestPdfFactory.CreateWithAttachment(p, "a.txt", [1, 2, 3]);
        var prompts = new FakePrompts();
        prompts.SavePicks.Enqueue(Path.Combine(Dir, "no-such-dir", "x.bin"));
        var vm = new AttachmentsViewModel(await OpenTab(p), prompts);
        await vm.LoadAsync();

        await vm.SaveAsync(vm.Items[0]);

        Assert.Single(prompts.Errors);
    }
}
