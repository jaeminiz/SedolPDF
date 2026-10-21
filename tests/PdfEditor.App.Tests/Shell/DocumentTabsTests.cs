using PdfEditor.App.Documents;
using PdfEditor.App.Shell;

namespace PdfEditor.App.Tests.Shell;

public class DocumentTabsTests
{
    [Theory]
    [InlineData(1000, 2, TabStripPanel.MaxTabWidth)]    // 여유 있으면 최대 폭
    [InlineData(1000, 0, TabStripPanel.MaxTabWidth)]
    [InlineData(10, 6, TabStripPanel.MinTabWidth)]      // 아무리 좁아도 최소 폭
    public void TabWidth_Clamps(double available, int count, double expected)
        => Assert.Equal(expected, TabStripPanel.TabWidth(available, count));

    [Fact]
    public void TabWidth_ShrinksEvenly_SoAllTabsFit()
    {
        double w = TabStripPanel.TabWidth(900, 6);
        Assert.InRange(w, TabStripPanel.MinTabWidth, TabStripPanel.MaxTabWidth);
        Assert.Equal(900, 6 * w + 5 * TabStripPanel.Gap, 6);
        Assert.True(TabStripPanel.TabWidth(900, 7) < w);
    }

    [Fact]
    public void ScrollOffset_OnlyWhenOverflowing()
    {
        Assert.Equal(0, TabStripPanel.ScrollOffset(500, 400, 400));
        Assert.Equal(300, TabStripPanel.ScrollOffset(500, 800, 800));   // 마지막 탭이 보이게
        Assert.Equal(0, TabStripPanel.ScrollOffset(500, 800, 200));     // 앞쪽 탭이면 그대로
    }

    private static readonly (double, double)[] Tabs = [(0, 100), (102, 100), (204, 100)];

    [Theory]
    [InlineData(10, 0)]
    [InlineData(60, 1)]    // 첫 탭 가운데를 넘으면 그 다음 앞
    [InlineData(160, 2)]
    [InlineData(290, 3)]   // 맨 끝
    public void Slot_FromPointer(double x, int expected)
        => Assert.Equal(expected, TabReorder.Slot(Tabs, x));

    [Theory]
    [InlineData(0, 3, 2)]   // 맨 끝으로
    [InlineData(2, 0, 0)]
    [InlineData(1, 1, 1)]   // 자기 앞 = 그대로
    [InlineData(1, 2, 1)]   // 자기 뒤 = 그대로
    [InlineData(0, 2, 1)]
    public void FinalIndex_Computes(int from, int slot, int expected)
        => Assert.Equal(expected, TabReorder.FinalIndex(from, slot, 3));

    private static (MainWindowViewModel Vm, DocumentTabViewModel[] Tabs) Make(int n)
    {
        var vm = new MainWindowViewModel();
        var tabs = Enumerable.Range(0, n).Select(i => new DocumentTabViewModel($"d{i}.pdf")).ToArray();
        foreach (var t in tabs) vm.Documents.Add(t);
        vm.ActiveDocument = tabs[1];
        return (vm, tabs);
    }

    [Fact]
    public void MoveDocument_ReordersAndKeepsActiveTab()
    {
        var (vm, t) = Make(4);
        Assert.True(vm.MoveDocument(t[0], 3));   // d0을 d3 앞으로
        Assert.Equal([t[1], t[2], t[0], t[3]], vm.Documents);
        Assert.Same(t[1], vm.ActiveDocument);
        Assert.True(vm.MoveDocument(t[3], 0));
        Assert.Equal([t[3], t[1], t[2], t[0]], vm.Documents);
        Assert.Same(t[1], vm.ActiveDocument);
    }

    [Fact]
    public void MoveDocument_NoChange_ReturnsFalse()
    {
        var (vm, t) = Make(3);
        Assert.False(vm.MoveDocument(t[1], 1));
        Assert.False(vm.MoveDocument(t[1], 2));
        Assert.Equal(t, vm.Documents);
        Assert.False(vm.MoveDocument(new DocumentTabViewModel("x.pdf"), 0));
    }

    [Fact]
    public void MoveDocument_MovingActiveTab_KeepsItActive()
    {
        var (vm, t) = Make(3);
        Assert.True(vm.MoveDocument(t[1], 0 + 3));
        Assert.Same(t[1], vm.ActiveDocument);
        Assert.Equal([t[0], t[2], t[1]], vm.Documents);
    }
}
