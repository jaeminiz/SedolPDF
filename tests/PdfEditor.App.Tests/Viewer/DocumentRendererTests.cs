using Pdf.Native;
using PdfEditor.App.Viewer;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
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
        return DocumentRenderer.TileRequestFor(info, new TileKey(info.Id, info.Version, info.AnnotRevision, TileKey.ToScaleKey(scale), 0, 0));
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
        _thumbs.Add(req.Key.Page, req.Key.Version, req.Key.AnnotRevision, new FakeImage(1, 1), 10);
        await _s.Commands.ExecuteAsync(new RotatePagesCommand([req.Key.Page], 1));
        Assert.False(_tiles.TryGet(req.Key, out _));
        Assert.True(img.Disposed);
        Assert.False(_thumbs.TryGet(req.Key.Page, req.Key.Version, req.Key.AnnotRevision, out _));
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

public class DocumentRendererAnnotationTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-dra-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private DocumentSession _s = null!;
    private TileCache<IImageHandle> _tiles = null!;
    private ThumbnailCache<IImageHandle> _thumbs = null!;
    private DocumentRenderer _r = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "m.pdf");
        AnnotatedPdfFactory.CreateMixed(path);
        _s = await DocumentSession.OpenAsync(_t, path, null);
        await _s.AnnotationsReady;
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

    private TileRequest Tile()
    {
        var info = _s.Pages[0];
        return DocumentRenderer.TileRequestFor(info, new TileKey(info.Id, info.Version, info.AnnotRevision, TileKey.ToScaleKey(1.0), 0, 0));
    }

    // 빨강 Square [1]: PDF (100,600)-(300,650), 쪽 높이 842 → 위에서 192~242pt. 왼쪽 테두리 위 한 점.
    private static bool IsRed(byte[] bgra, int w) { int o = (217 * w + 101) * 4; return bgra[o + 2] > 200 && bgra[o + 1] < 80 && bgra[o] < 80; }
    private static bool IsWhite(byte[] bgra, int w) { int o = (217 * w + 101) * 4; return bgra[o] > 240 && bgra[o + 1] > 240 && bgra[o + 2] > 240; }

    [Fact]
    public async Task TouchedOriginal_HiddenInNewTile_VisibleInOldTile()
    {
        var page = _s.Pages[0].Id;
        var oldReq = Tile();
        var oldData = await _r.RenderAsync(oldReq, CancellationToken.None);
        Assert.NotNull(oldData);
        Assert.True(IsRed(oldData!, oldReq.Rect.W));
        Assert.True(_r.Accept(oldReq, oldData!));

        var sq = _s.Annotations.Get(page).Items.First(i => i.Data.Kind == AnnotKind.Square && Math.Abs(i.Data.Rect.Left - 100) < 1).Data;
        await _s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(page, [(sq, AnnotationGeometry.Translate(sq, 200, 0))]));

        Assert.Equal(1, _s.Pages[0].AnnotRevision);
        // 옛 타일은 남아 있고(깜빡임 없음), 옛 요청 결과는 버려진다
        Assert.True(_tiles.TryGet(oldReq.Key, out _));
        Assert.Null(await _r.RenderAsync(oldReq, CancellationToken.None));
        Assert.False(_r.Accept(oldReq, oldData!));

        var req = Tile();
        Assert.Equal(1, req.Key.AnnotRevision);
        var data = await _r.RenderAsync(req, CancellationToken.None);
        Assert.NotNull(data);
        Assert.True(IsWhite(data!, req.Rect.W));
        Assert.True(_r.Accept(req, data!));
        Assert.False(_tiles.TryGet(oldReq.Key, out _));   // 새 타일이 옛 타일을 대신
    }

    [Fact]
    public async Task StaleAnnotRevisionResult_Dropped()
    {
        var req = Tile();
        var data = await _r.RenderAsync(req, CancellationToken.None);
        var page = _s.Pages[0].Id;
        var sq = _s.Annotations.Get(page).Items.First(i => i.Data.Kind == AnnotKind.Square && Math.Abs(i.Data.Rect.Left - 100) < 1).Data;
        await _s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(page, [(sq, AnnotationGeometry.Translate(sq, 200, 0))]));
        Assert.False(_r.Accept(req, data!));   // 렌더 중 AnnotRevision이 올랐다
        Assert.False(_tiles.TryGet(req.Key, out _));
    }
}
