using Pdf.Native;
using PdfEditor.Document;

namespace PdfEditor.App.Viewer;

/// <summary>화면 이미지 한 장. 앱에서는 WriteableBitmap을 감싼다(BGRA, 불투명). 테스트에서는 가짜로 바꾼다.</summary>
public interface IImageHandle : IDisposable
{
    int Width { get; }
    int Height { get; }
}

/// <summary>
/// 화면과 PDFium 사이(UI 없음). 렌더 요청을 PDFium 전용 스레드에서 실행하고, 결과를 캐시에 넣고,
/// 페이지가 바뀌면 오래된 캐시를 지운다. 페이지는 항상 PageId로 찾는다(Review Focus 1).
/// </summary>
public sealed class DocumentRenderer : IDisposable
{
    private readonly DocumentSession _s;
    private readonly TileCache<IImageHandle> _tiles;
    private readonly ThumbnailCache<IImageHandle> _thumbs;
    private readonly Func<byte[], int, int, IImageHandle> _createImage;
    private readonly object _lock = new();
    private HashSet<PageId> _known;
    private bool _disposed;

    public DocumentRenderer(DocumentSession s, TileCache<IImageHandle> tiles, ThumbnailCache<IImageHandle> thumbs,
        Func<byte[], int, int, IImageHandle> createImage)
    {
        _s = s;
        _tiles = tiles;
        _thumbs = thumbs;
        _createImage = createImage;
        _known = s.Pages.Select(p => p.Id).ToHashSet();
        s.PagesChanged += OnPagesChanged;
    }

    /// <summary>PagesChanged 처리(캐시 정리)가 끝난 뒤 발생한다. 전용 스레드에서 불리므로 UI는 디스패치해야 한다.</summary>
    public event Action? Invalidated;

    public static TileRequest TileRequestFor(PageInfo page, TileKey key) =>
        new(TileKind.Tile, key, key.Scale, key.PixelBounds(page));

    public static TileRequest ThumbnailRequestFor(PageInfo page)
    {
        var (w, h) = ThumbnailCache<IImageHandle>.SizeFor(page);
        var key = new TileKey(page.Id, page.Version, page.AnnotRevision, 0, 0, 0);
        return new TileRequest(TileKind.Thumbnail, key, w / (double)page.WidthPt, new PixelRect(0, 0, w, h));
    }

    /// <summary>벤치용: 타일(썸네일 제외) 한 장을 PDFium 스레드에서 렌더한 시간(ms). 평소에는 null.</summary>
    public static Action<double>? TileRenderTimed;

    /// <summary>
    /// 전용 스레드에서 페이지를 PageId로 다시 찾고, 없거나 버전이 다르면 null.
    /// 그렇지 않으면 BGRA 바이트(타일 크기 × 4)를 돌려준다.
    /// 화면 타일은 PDFium 우선 대기열로 보낸다: 큰 도면의 썸네일은 한 장에 수백 ms 걸려서,
    /// 같은 줄에 서면 보이는 타일 사이에 끼어 첫 화면이 늦어진다(M1 마무리 검토에서 측정).
    /// </summary>
    public Task<byte[]?> RenderAsync(TileRequest r, CancellationToken ct)
    {
        Func<byte[]?> work = () =>
        {
            ct.ThrowIfCancellationRequested();
            if (!IsCurrent(r.Key)) return null;
            if (_s.IndexOf(r.Key.Page) < 0) return null;
            var page = _s.GetLoadedPage(r.Key.Page);   // 핸들은 이 호출 안에서만 쓴다
            var timed = TileRenderTimed;
            long t0 = timed is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            var bytes = page.RenderTile(r.Scale, r.Rect.X, r.Rect.Y, r.Rect.W, r.Rect.H, new RenderOptions(WithAnnotations: true, HiddenAnnotIndices: _s.Annotations.HiddenPdfIndices(r.Key.Page)));
            if (timed is not null && r.Kind == TileKind.Tile) timed(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            return bytes;
        };
        return r.Kind != TileKind.Thumbnail ? _s.Thread.InvokeHighAsync(work, ct) : _s.Thread.InvokeAsync(work, ct);
    }

    private bool IsCurrent(TileKey key)
    {
        foreach (var p in _s.Pages)
            if (p.Id == key.Page) return p.Version == key.Version && p.AnnotRevision == key.AnnotRevision;
        return false;
    }

    /// <summary>렌더 결과를 이미지로 만들어 캐시에 넣는다. 그 사이 페이지가 바뀌었으면 버리고 false. UI 스레드에서 부른다.</summary>
    public bool Accept(TileRequest r, byte[] bgra)
    {
        lock (_lock)
        {
            if (_disposed || !IsCurrent(r.Key)) return false;
            var img = _createImage(bgra, r.Rect.W, r.Rect.H);
            long bytes = (long)r.Rect.W * r.Rect.H * 4;
            if (r.Kind == TileKind.Thumbnail) _thumbs.Add(r.Key.Page, r.Key.Version, r.Key.AnnotRevision, img, bytes);
            else _tiles.Add(r.Key, img, bytes);
            return true;
        }
    }

    private void OnPagesChanged(PagesChangedEventArgs e)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var pages = _s.Pages;
            var now = new HashSet<PageId>(pages.Select(p => p.Id));
            foreach (var gone in _known.Where(id => !now.Contains(id)))
            {
                _tiles.RemovePage(gone);
                _thumbs.RemovePage(gone);
            }
            foreach (var p in pages)
            {
                _tiles.RemoveStaleVersions(p.Id, p.Version);
                _thumbs.RemoveStaleVersions(p.Id, p.Version);
            }
            _known = now;
        }
        Invalidated?.Invoke();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _s.PagesChanged -= OnPagesChanged;
    }
}
