using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

public class BookmarksViewModelTests : PanelTestBase
{
    [Fact]
    public async Task Bookmarks_Tree_AndNavigate()
    {
        string p = Path.Combine(Dir, "o.pdf");
        TestPdfFactory.CreateWithOutline(p);
        var tab = await OpenTab(p);
        var vm = new BookmarksViewModel(tab);

        await vm.LoadAsync();

        Assert.Equal(2, vm.Nodes.Count);
        Assert.Equal("1장", vm.Nodes[0].Title);
        Assert.True(vm.Nodes[0].IsExpanded);          // InitiallyOpen 반영
        Assert.False(vm.Nodes[1].IsExpanded);
        var child = Assert.Single(vm.Nodes[0].Children);
        Assert.Equal("1.1 절", child.Title);
        Assert.False(vm.IsEmpty);

        vm.Navigate(child);
        Assert.Equal(1, tab.CurrentPageIndex);
        vm.Navigate(vm.Nodes[1]);
        Assert.Equal(2, tab.CurrentPageIndex);
    }

    [Fact]
    public async Task Bookmarks_Navigate_NullPageIndex_DoesNothing()
    {
        string p = Path.Combine(Dir, "o.pdf");
        TestPdfFactory.CreateWithOutline(p);
        var tab = await OpenTab(p);
        var vm = new BookmarksViewModel(tab);
        tab.CurrentPageIndex = 2;

        vm.Navigate(new BookmarkNode("no-dest", null, [], false));

        Assert.Equal(2, tab.CurrentPageIndex);
    }

    [Fact]
    public async Task Bookmarks_None_ShowsEmptyMessage()
    {
        string p = Path.Combine(Dir, "b.pdf");
        TestPdfFactory.CreateBlank(p, 2, 595, 842);
        var tab = await OpenTab(p);
        var vm = new BookmarksViewModel(tab);

        await vm.LoadAsync();

        Assert.Empty(vm.Nodes);
        Assert.True(vm.IsEmpty);
        Assert.Equal(Strings.Panel_NoBookmarks, vm.EmptyText);
    }

    [Fact]
    public async Task Bookmarks_LoadTwice_ReadsOnce()
    {
        string p = Path.Combine(Dir, "o.pdf");
        TestPdfFactory.CreateWithOutline(p);
        var vm = new BookmarksViewModel(await OpenTab(p));
        await vm.LoadAsync();
        var first = vm.Nodes[0];
        await vm.LoadAsync();
        Assert.Same(first, vm.Nodes[0]);
    }
}
