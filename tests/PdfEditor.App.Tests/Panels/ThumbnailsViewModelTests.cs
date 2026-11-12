using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Settings;
using PdfEditor.App.Tests.Documents;
using PdfEditor.App.Viewer;
using PdfEditor.Document;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

public class ThumbnailsViewModelTests : IAsyncLifetime
{
    private sealed class FakeImage(int w, int h) : IImageHandle
    {
        public int Width { get; } = w;
        public int Height { get; } = h;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-th-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly List<DocumentSession> _sessions = [];

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static async Task WaitFor(Func<bool> cond, int ms = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            Assert.True(sw.ElapsedMilliseconds < ms, "timeout");
            await Task.Delay(20);
        }
    }

    private async Task<(DocumentSession S, DocumentTabViewModel Tab, ThumbnailsViewModel Vm, PageCommandsViewModel Cmds)> SetUp(int pages)
    {
        string path = Path.Combine(_dir, "t.pdf");
        TestPdfFactory.CreateLabeled(path, Enumerable.Range(0, pages).Select(i => (100f + 10 * i, 200f)).ToList());
        var prompts = new FakePrompts();
        var wf = new FileWorkflow(_t, new SaveService([]), new SettingsStore(Path.Combine(_dir, "s.json")), prompts);
        var s = (await wf.OpenAsync(path))!;
        _sessions.Add(s);
        var tab = new DocumentTabViewModel(s, "t", null);
        var vm = new ThumbnailsViewModel(tab, (_, w, h) => new FakeImage(w, h));
        return (s, tab, vm, new PageCommandsViewModel(tab, wf, prompts));
    }

    [Fact]
    public async Task Thumbnails_InitialList_MatchesPages_AndLoadsVisibleImages()
    {
        var (s, _, vm, _) = await SetUp(3);
        Assert.Equal(s.Pages.Select(p => p.Id), vm.Items.Select(i => i.Id));
        Assert.Equal([1, 2, 3], vm.Items.Select(i => i.PageNumber));
        Assert.All(vm.Items, i => Assert.Null(i.Image));
        foreach (var i in vm.Items) vm.MarkVisible(i.Id);
        await WaitFor(() => vm.Items.All(i => i.Image is not null));
        Assert.All(vm.Items, i => Assert.Equal(ThumbnailCache<IImageHandle>.LongSidePx, Math.Max(i.Image!.Width, i.Image.Height)));
    }

    [Fact]
    public async Task Thumbnails_Size_IsClamped_AndChangesCellAndDisplaySize()
    {
        var (_, _, vm, _) = await SetUp(3);
        Assert.Equal(160, vm.Size);
        Assert.Equal(160 + ThumbnailsViewModel.CellPadding, vm.CellWidth);
        vm.Size = 5;   Assert.Equal(ThumbnailsViewModel.MinSize, vm.Size);
        vm.Size = 9999; Assert.Equal(ThumbnailsViewModel.MaxSize, vm.Size);
        vm.Size = double.NaN; Assert.Equal(ThumbnailsViewModel.MaxSize, vm.Size);
        vm.Size = 100.4;
        Assert.Equal(100, vm.Size);
        Assert.Equal(100 + ThumbnailsViewModel.CellPadding + ThumbnailsViewModel.CellLabelHeight, vm.CellHeight);
        // 쪽 크기 (100+10i)×200 → 긴 변이 Size
        Assert.All(vm.Items, i => Assert.Equal(100, Math.Max(i.DisplayWidth, i.DisplayHeight)));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.Size = 120;
        Assert.Contains(nameof(ThumbnailsViewModel.CellWidth), changed);
        Assert.Contains(nameof(ThumbnailsViewModel.CellHeight), changed);
    }

    [Fact]
    public async Task Thumbnails_SmallSizes_ReuseTheSameRenderedImages()
    {
        var (_, _, vm, _) = await SetUp(2);
        foreach (var i in vm.Items) vm.MarkVisible(i.Id);
        await WaitFor(() => vm.Items.All(i => i.Image is not null));
        var before = vm.Items.Select(i => i.Image).ToList();
        vm.Size = 90;   // 160 이하는 같은 160px 이미지를 줄여 쓴다
        Assert.Equal(160, vm.RenderLongSide);
        Assert.Equal(before, vm.Items.Select(i => i.Image).ToList());
    }

    [Fact]
    public async Task Thumbnails_LargeSizes_RerenderAt256_AndBackTo160()
    {
        var (_, _, vm, _) = await SetUp(3);
        foreach (var i in vm.Items) vm.MarkVisible(i.Id);
        await WaitFor(() => vm.Items.All(i => i.Image is not null));
        var small = vm.Items.Select(i => i.Image!).ToList();

        vm.Size = 220;
        Assert.Equal(256, vm.RenderLongSide);
        await WaitFor(() => vm.Items.All(i => i.Image is { } img && Math.Max(img.Width, img.Height) == 256));
        await WaitFor(() => small.All(i => ((FakeImage)i).Disposed));   // 옛 크기 이미지는 해제된다

        vm.Size = 150;
        await WaitFor(() => vm.Items.All(i => i.Image is { } img && Math.Max(img.Width, img.Height) == 160));
    }

    [Fact]
    public async Task Thumbnails_AfterDelete_ListMatchesPages_KeepsImagesForSameIds()
    {
        var (s, tab, vm, cmds) = await SetUp(4);
        foreach (var i in vm.Items) vm.MarkVisible(i.Id);
        await WaitFor(() => vm.Items.All(i => i.Image is not null));
        var before = vm.Items.ToDictionary(i => i.Id, i => i.Image!);
        var removed = s.Pages[1].Id;

        tab.SelectedPages = new HashSet<PageId> { removed };
        await cmds.DeleteCommand.ExecuteAsync(null);
        await WaitFor(() => vm.Items.Count == 3);

        Assert.Equal(s.Pages.Select(p => p.Id), vm.Items.Select(i => i.Id));
        Assert.Equal([1, 2, 3], vm.Items.Select(i => i.PageNumber));
        foreach (var item in vm.Items)
        {
            Assert.Same(before[item.Id], item.Image);
            Assert.False(((FakeImage)item.Image!).Disposed);
        }
        await WaitFor(() => ((FakeImage)before[removed]).Disposed);   // 지운 쪽의 이미지는 해제된다

        await cmds.UndoCommand.ExecuteAsync(null);
        await WaitFor(() => vm.Items.Count == 4);
        Assert.Equal(s.Pages.Select(p => p.Id), vm.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Thumbnails_Rotate_RerendersWithNewVersion()
    {
        var (s, _, vm, cmds) = await SetUp(2);
        foreach (var i in vm.Items) vm.MarkVisible(i.Id);
        await WaitFor(() => vm.Items.All(i => i.Image is not null));
        var old = vm.Items[0].Image!;
        await cmds.RotateRightCommand.ExecuteAsync(null);
        await WaitFor(() => vm.Items[0].Image is { } img && !ReferenceEquals(img, old));
        Assert.Equal(s.Pages[0].Version, vm.Items[0].Version);
    }

    [Fact]
    public async Task Thumbnails_Selection_UpdatesTabAndCurrentPage()
    {
        var (s, tab, vm, _) = await SetUp(3);
        vm.SetSelection([vm.Items[0], vm.Items[2]], vm.Items[2]);
        Assert.Equal(new HashSet<PageId> { s.Pages[0].Id, s.Pages[2].Id }, tab.SelectedPages.ToHashSet());
        Assert.Equal(2, tab.CurrentPageIndex);
        Assert.True(vm.Items[2].IsCurrent);
        Assert.False(vm.Items[0].IsCurrent);
        tab.CurrentPageIndex = 1;
        Assert.True(vm.Items[1].IsCurrent);
        Assert.False(vm.Items[2].IsCurrent);
    }
}
