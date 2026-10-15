using Pdf.Native;
using PdfEditor.App.Viewer;
using PdfEditor.Document;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Viewer;

public sealed class FakeImage(int w, int h) : IImageHandle
{
    public int Width { get; } = w;
    public int Height { get; } = h;
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

public class DocumentRendererTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-dr-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private DocumentSession _s = null!;
    private TileCache<IImageHandle> _tiles = null!;
    private ThumbnailCache<IImageHandle> _thumbs = null!;
    private DocumentRenderer _r = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "a.pdf");
        TestPdfFactory.CreateLabeled(path, [(200, 300), (300, 200), (200, 200)]);
        _s = await DocumentSession.OpenAsync(_t, path, null);
        _tiles = new TileCache<IImageHandle>(1L << 30, i => i.Dispose());
        _thumbs = new ThumbnailCache<IImageHandle>(1L << 30, i => i.Dispose());
        _r = new DocumentRenderer(_s, _tiles, _thumbs, (_, w, h) => new FakeImage(w, h));
    }

    public async Task DisposeAsync()
    {
        _r.Dispose();
        await _s.DisposeAsync();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private TileRequest TileOf(int pageIndex, double scale = 1.0)
    {
        var info = _s.Pages[pageIndex];
        return DocumentRenderer.TileRequestFor(info, new TileKey(info.Id, info.Version, TileKey.ToScaleKey(scale), 0, 0));
    }

    [Fact]
    public async Task RenderAsync_ReturnsBgraOfTileSize()
    {
        var req = TileOf(0, 1.0);
        var data = await _r.RenderAsync(req, CancellationToken.None);
        Assert.NotNull(data);
        Assert.Equal(200 * 300 * 4, data!.Length);
        Assert.Equal(200, req.Rect.W);
        Assert.Equal(300, req.Rect.H);
        // 흰 배경 위의 검정 표식(왼쪽 위 20x20pt)
        Assert.Equal(0, data[(5 * 200 + 5) * 4]);
        Assert.Equal(255, data[(100 * 200 + 150) * 4]);
    }

    [Fact]
    public async Task RenderAsync_AfterDelete_ReturnsNullForRemovedPage()
    {
        var req = TileOf(1);
        await _s.Commands.ExecuteAsync(new DeletePagesCommand([req.Key.Page]));
        Assert.Null(await _r.RenderAsync(req, CancellationToken.None));
        // 남은 페이지는 계속 그려진다
        Assert.NotNull(await _r.RenderAsync(TileOf(1), CancellationToken.None));
    }

    [Fact]
    public async Task RenderAsync_AfterRotate_OldVersionReturnsNull()
    {
        var req = TileOf(0);
        await _s.Commands.ExecuteAsync(new RotatePagesCommand([req.Key.Page], 1));
        Assert.Null(await _r.RenderAsync(req, CancellationToken.None));
        Assert.NotNull(await _r.RenderAsync(TileOf(0), CancellationToken.None)); // 새 버전은 그려진다
    }

    [Fact]
    public async Task RenderAsync_AfterMove_FollowsPageId()
    {
        var req = TileOf(0);     // 200x300 페이지
        await _s.Commands.ExecuteAsync(new MovePagesCommand([req.Key.Page], 2));
        var data = await _r.RenderAsync(req, CancellationToken.None);
        Assert.NotNull(data);
        Assert.Equal(200 * 300 * 4, data!.Length); // 인덱스가 아니라 PageId로 찾으므로 같은 페이지
    }

    [Fact]
    public async Task PagesChanged_Rotate_EvictsOldVersionTiles()
    {
        var req = TileOf(0);
        var img = new FakeImage(200, 300);
        _tiles.Add(req.Key, img, 1000);
        _thumbs.Add(req.Key.Page, req.Key.Version, new FakeImage(1, 1), 10);
        await _s.Commands.ExecuteAsync(new RotatePagesCommand([req.Key.Page], 1));
        Assert.False(_tiles.TryGet(req.Key, out _));
        Assert.True(img.Disposed);
        Assert.False(_thumbs.TryGet(req.Key.Page, req.Key.Version, out _));
    }

    [Fact]
    public async Task PagesChanged_Delete_RemovesPageFromCaches()
    {
        var req = TileOf(2);
        _tiles.Add(req.Key, new FakeImage(1, 1), 10);
        await _s.Commands.ExecuteAsync(new DeletePagesCommand([req.Key.Page]));
        Assert.False(_tiles.TryGet(req.Key, out _));
    }

    [Fact]
    public async Task Thumbnail_LongSide160()
    {
        var info = _s.Pages[1]; // 300x200 가로
        var req = DocumentRenderer.ThumbnailRequestFor(info);
        Assert.Equal(TileKind.Thumbnail, req.Kind);
        var data = await _r.RenderAsync(req, CancellationToken.None);
        Assert.NotNull(data);
        Assert.Equal(160, req.Rect.W);
        Assert.Equal(107, req.Rect.H);
        Assert.Equal(req.Rect.W * req.Rect.H * 4, data!.Length);
    }

    [Fact]
    public async Task Accept_StoresImageInCache_AndDropsStaleVersion()
    {
        var req = TileOf(0);
        var data = await _r.RenderAsync(req, CancellationToken.None);
        Assert.True(_r.Accept(req, data!));
        Assert.True(_tiles.TryGet(req.Key, out var img));
        Assert.Equal(200, img.Width);

        await _s.Commands.ExecuteAsync(new RotatePagesCommand([req.Key.Page], 1));
        Assert.False(_r.Accept(req, data!)); // 늦게 도착한 옛 버전 결과는 버린다
        Assert.False(_tiles.TryGet(req.Key, out _));
    }
}
