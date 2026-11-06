using PdfEditor.App.Documents;
using PdfEditor.App.Settings;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Documents;
using PdfEditor.App.Tests.Panels;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;
using Xunit;

namespace PdfEditor.App.Tests.Hosting;

/// <summary>0.8.0 단계 3: 세션을 닫지 않고 한 창(뷰모델)의 탭을 빼서 다른 창에 넣는다.</summary>
public sealed class TabTransferViewModelTests : PanelTestBase
{
    private readonly FakePrompts _p = new();
    private FileWorkflow? _wf;

    private FileWorkflow Wf() => _wf ??= new FileWorkflow(Thread, new SaveService([]), new SettingsStore(Path.Combine(Dir, "s.json")), _p);

    private MainWindowViewModel NewVm()
    {
        var vm = new MainWindowViewModel();
        vm.AttachWorkflow(Wf(), _p, a => a());
        return vm;
    }

    private string ThreePages()
    {
        string p = Path.Combine(Dir, "three.pdf");
        TestPdfFactory.CreateLabeled(p, [(400, 600), (400, 600), (400, 600), (400, 600)]);
        return p;
    }

    [Fact]
    public async Task Transfer_KeepsSameSessionDirtyAndUndo()
    {
        var vm1 = NewVm();
        var vm2 = NewVm();
        await vm1.OpenAsync(ThreePages());
        var tab = vm1.ActiveDocument!;
        var s = tab.Session!;
        await s.Commands.ExecuteAsync(new DeletePagesCommand([s.Pages[1].Id]));
        Assert.Equal(3, s.Pages.Length);

        vm2.AcceptTransfer(vm1.DetachForTransfer(tab)!, 0);

        Assert.Empty(vm1.Documents);
        Assert.Null(vm1.ActiveDocument);
        var moved = Assert.Single(vm2.Documents);
        Assert.Same(moved, vm2.ActiveDocument);
        Assert.NotSame(tab, moved);
        Assert.Same(s, moved.Session);
        Assert.True(s.IsDirty);
        Assert.True(s.Commands.CanUndo);
        await s.Commands.UndoAsync();
        Assert.Equal(4, s.Pages.Length);
    }

    [Fact]
    public async Task Transfer_DoesNotReleaseOrDispose()
    {
        var vm1 = NewVm();
        var vm2 = NewVm();
        await vm1.OpenAsync(ThreePages());
        var tab = vm1.ActiveDocument!;
        var s = tab.Session!;
        await s.Commands.ExecuteAsync(new DeletePagesCommand([s.Pages[1].Id]));

        vm2.AcceptTransfer(vm1.DetachForTransfer(tab)!, 0);

        Assert.Contains(s, Wf().OpenSessions);
        Assert.True(await Wf().SaveAsync(s));
        Assert.False(s.IsDirty);
        await vm2.CloseDocumentAsync(vm2.ActiveDocument!);
    }

    [Fact]
    public async Task Transfer_KeepsPageAndScale()
    {
        var vm1 = NewVm();
        var vm2 = NewVm();
        await vm1.OpenAsync(ThreePages());
        var tab = vm1.ActiveDocument!;
        tab.CurrentPageIndex = 3;
        tab.Scale = 1.5;
        vm2.AcceptTransfer(vm1.DetachForTransfer(tab)!, 0);
        Assert.Equal(3, vm2.ActiveDocument!.CurrentPageIndex);
        Assert.Equal(1.5, vm2.ActiveDocument!.Scale, 6);

        // 제목 없는 새 문서도 그대로(쪽 하나라 쪽 0)
        vm1.ShowSession(await Wf().NewAsync());
        var untitled = vm1.ActiveDocument!;
        untitled.Scale = 1.5;
        vm2.AcceptTransfer(vm1.DetachForTransfer(untitled)!, 0);
        Assert.Same(untitled.Session, vm2.Documents[0].Session);
        Assert.Equal(1.5, vm2.Documents[0].Scale, 6);
        Assert.Equal(untitled.Title, vm2.Documents[0].Title);
    }

    [Fact]
    public async Task Transfer_OldTabNoLongerReceivesSessionEvents()
    {
        var vm1 = NewVm();
        var vm2 = NewVm();
        await vm1.OpenAsync(ThreePages());
        var tab = vm1.ActiveDocument!;
        var s = tab.Session!;
        vm2.AcceptTransfer(vm1.DetachForTransfer(tab)!, 0);
        var moved = vm2.ActiveDocument!;

        int oldTitle = 0, newTitle = 0;
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DocumentTabViewModel.Title)) oldTitle++; };
        moved.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DocumentTabViewModel.Title)) newTitle++; };
        await s.Commands.ExecuteAsync(new DeletePagesCommand([s.Pages[1].Id]));
        Assert.Equal(0, oldTitle);
        Assert.True(newTitle > 0);
        Assert.Equal(3, moved.PageCount);
    }

    [Fact]
    public async Task Transfer_BusySession_IsRefused()
    {
        var vm1 = NewVm();
        await vm1.OpenAsync(ThreePages());
        var tab = vm1.ActiveDocument!;
        using (Wf().MarkBusy(tab.Session!))
            Assert.Null(vm1.DetachForTransfer(tab));
        Assert.Same(tab, Assert.Single(vm1.Documents));
        Assert.Same(tab, vm1.ActiveDocument);
    }

    [Fact]
    public async Task Detach_MiddleTab_ActivatesNeighbour_AndAcceptClampsSlot()
    {
        var vm1 = NewVm();
        var vm2 = NewVm();
        string a = Path.Combine(Dir, "a.pdf"), b = Path.Combine(Dir, "b.pdf"), c = Path.Combine(Dir, "c.pdf");
        foreach (var f in new[] { a, b, c }) TestPdfFactory.CreateBlank(f, 1, 200, 200);
        await vm1.OpenAsync(a);
        await vm1.OpenAsync(b);
        await vm1.OpenAsync(c);
        var mid = vm1.Documents[1];
        vm1.ActiveDocument = mid;
        var packet = vm1.DetachForTransfer(mid)!;
        Assert.Equal(2, vm1.Documents.Count);
        Assert.Same(vm1.Documents[1], vm1.ActiveDocument);   // 닫기와 같은 규칙: 같은 자리(오른쪽 이웃)

        await vm2.OpenAsync(ThreePages());
        vm2.AcceptTransfer(packet, 99);
        Assert.Same(packet.Session, vm2.Documents[^1].Session);
    }
}
