using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

public class SearchViewModelTests : PanelTestBase
{
    private static readonly (float W, float H) A4 = (595, 842);

    private string Labeled(int pages)
    {
        string p = Path.Combine(Dir, $"l{pages}.pdf");
        TestPdfFactory.CreateLabeled(p, Enumerable.Repeat(A4, pages).ToList());
        return p;
    }

    [Fact]
    public async Task Search_FindsAcrossPages_InPageOrderFromCurrent()
    {
        var tab = await OpenTab(Labeled(3));
        tab.CurrentPageIndex = 1;
        var vm = new SearchViewModel(tab) { Query = "PAGE" };

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal([1, 2, 0], vm.Results.Select(h => h.PageIndexAtSearch).ToArray());
        Assert.All(vm.Results, h =>
        {
            Assert.Equal(4, h.CharCount);
            Assert.Contains("PAGE", h.Context);
            Assert.NotEmpty(h.RectsPt);
        });
        Assert.False(vm.IsSearching);
        Assert.Equal(string.Format(Strings.Search_Progress, 3, 3, 3), vm.StatusText);
    }

    [Fact]
    public async Task Search_MatchCase_Respected()
    {
        var tab = await OpenTab(Labeled(2));
        var vm = new SearchViewModel(tab) { Query = "page" };
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Results.Count);

        vm.MatchCase = true;
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Empty(vm.Results);
        Assert.Equal(Strings.Search_NoResults, vm.StatusText);
    }

    [Fact]
    public async Task Search_Korean()
    {
        string p = Path.Combine(Dir, "k.pdf");
        TestPdfFactory.CreateKoreanText(p, "안녕하세요 검색 시험입니다");
        var tab = await OpenTab(p);
        var vm = new SearchViewModel(tab) { Query = "시험" };

        await vm.SearchCommand.ExecuteAsync(null);

        var hit = Assert.Single(vm.Results);
        Assert.Equal(2, hit.CharCount);
        Assert.Contains("검색", hit.Context);
        Assert.Contains("시험", hit.Context);
    }

    [Fact]
    public async Task Search_Cancel_StopsBetweenPages()
    {
        var tab = await OpenTab(Labeled(300));
        var vm = new SearchViewModel(tab) { Query = "PAGE" };

        var task = vm.SearchCommand.ExecuteAsync(null);
        Assert.True(vm.IsSearching);
        vm.CancelCommand.Execute(null);
        await task;

        Assert.False(vm.IsSearching);
        Assert.True(vm.Results.Count < 300, $"results {vm.Results.Count}");
    }

    [Fact]
    public async Task Search_NoTextLayer_ShowsMessage()
    {
        string p = Path.Combine(Dir, "blank.pdf");
        TestPdfFactory.CreateBlank(p, 3, 595, 842);
        var tab = await OpenTab(p);
        var vm = new SearchViewModel(tab) { Query = "abc" };

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Empty(vm.Results);
        Assert.Equal(Strings.Msg_NoTextLayer, vm.StatusText);
    }

    [Fact]
    public async Task Search_Interleaves_OnePageInvokePerPage()
    {
        var tab = await OpenTab(Labeled(300));
        var vm = new SearchViewModel(tab) { Query = "PAGE" };

        var search = vm.SearchCommand.ExecuteAsync(null);
        var other = tab.Session!.Thread.InvokeAsync(() => 42);
        Assert.Equal(42, await other);

        Assert.False(search.IsCompleted);   // 다른 작업이 검색 도중에 끼어들어 끝났다
        await search;
        Assert.Equal(300, vm.Results.Count);
    }

    [Fact]
    public async Task NextPrev_WrapAndNavigate()
    {
        var tab = await OpenTab(Labeled(3));
        var vm = new SearchViewModel(tab) { Query = "PAGE" };
        await vm.SearchCommand.ExecuteAsync(null);

        vm.NextCommand.Execute(null);
        Assert.Same(vm.Results[0], vm.CurrentHit);
        Assert.Equal(0, tab.CurrentPageIndex);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.Equal(2, tab.CurrentPageIndex);
        vm.NextCommand.Execute(null);   // 처음으로 돌아간다
        Assert.Same(vm.Results[0], vm.CurrentHit);
        vm.PrevCommand.Execute(null);   // 끝으로 돌아간다
        Assert.Same(vm.Results[2], vm.CurrentHit);
        Assert.Equal(2, tab.CurrentPageIndex);
    }

    [Fact]
    public async Task Clear_EmptiesResults()
    {
        var tab = await OpenTab(Labeled(3));
        var vm = new SearchViewModel(tab) { Query = "PAGE" };
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Results.Count);
        vm.Clear();
        Assert.Empty(vm.Results);
        Assert.Null(vm.CurrentHit);
    }
}
