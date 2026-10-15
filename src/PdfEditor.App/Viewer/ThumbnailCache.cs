using PdfEditor.Document;

namespace PdfEditor.App.Viewer;

/// <summary>페이지 긴 변 160px 미리보기. 키 (PageId, Version), 예산 32 MB LRU. TileCache를 키만 바꿔 재사용한다.</summary>
public sealed class ThumbnailCache<TImage> where TImage : class
{
    public const int LongSidePx = 160;
    public const long DefaultBudgetBytes = 32L * 1024 * 1024;

    private readonly TileCache<TImage> _cache;

    public ThumbnailCache(long budgetBytes, Action<TImage> onEvict) => _cache = new TileCache<TImage>(budgetBytes, onEvict);
    public ThumbnailCache(Action<TImage> onEvict) : this(DefaultBudgetBytes, onEvict) { }

    private static TileKey Key(PageId page, int version) => new(page, version, 0, 0, 0);

    public bool TryGet(PageId page, int version, out TImage image) => _cache.TryGet(Key(page, version), out image);
    public void Add(PageId page, int version, TImage image, long bytes) => _cache.Add(Key(page, version), image, bytes);
    public void RemovePage(PageId page) => _cache.RemovePage(page);
    public void RemoveStaleVersions(PageId page, int currentVersion) => _cache.RemoveStaleVersions(page, currentVersion);
    public long UsedBytes => _cache.UsedBytes;

    /// <summary>긴 변이 LongSidePx가 되는 썸네일 픽셀 크기.</summary>
    public static (int W, int H) SizeFor(PageInfo page)
    {
        double k = LongSidePx / Math.Max(page.WidthPt, page.HeightPt);
        return (Math.Max(1, (int)Math.Round(page.WidthPt * k)), Math.Max(1, (int)Math.Round(page.HeightPt * k)));
    }
}
