using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEditor.App.Documents;
using PdfEditor.App.Viewer;
using PdfEditor.Document;

namespace PdfEditor.App.Panels;

/// <summary>썸네일 목록의 한 칸. 같은 PageId면 쪽이 옮겨져도 같은 객체를 유지해 이미지를 다시 그리지 않는다.</summary>
public sealed partial class ThumbnailItem(PageId id) : ObservableObject
{
    public PageId Id { get; } = id;

    [ObservableProperty] private int _pageNumber;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private double _displayWidth = ThumbnailCache<IImageHandle>.LongSidePx;
    [ObservableProperty] private double _displayHeight = ThumbnailCache<IImageHandle>.LongSidePx;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Source))] private IImageHandle? _image;

    /// <summary>이미지가 마지막으로 맞춰진 쪽 버전. 회전 등으로 쪽 버전이 바뀌면 새로 그린다.</summary>
    public int Version { get => _version; set => SetProperty(ref _version, value); }
    private int _version = -1;

    /// <summary>주석 층(ThumbnailAnnotationLayer)이 모델을 읽는 세션.</summary>
    public PdfEditor.Document.DocumentSession? Session { get; set; }
    public int AnnotRevision { get; set; } = -1;

    /// <summary>화면에 그릴 이미지(IImageHandle이 BitmapHandle일 때).</summary>
    public IImage? Source => (Image as BitmapHandle)?.Bitmap;
}

/// <summary>
/// 왼쪽 썸네일 패널의 모델. 목록은 세션의 Pages를 따라가고(PagesChanged는 전용 스레드에서 오므로 post로 UI에 넘김),
/// 화면에 보이는(실체화된) 칸만 렌더를 요청한다. 선택은 탭의 SelectedPages로, 클릭은 현재 쪽으로 이어진다.
/// 모든 공개 메서드는 UI 스레드에서 부른다.
/// </summary>
public sealed class ThumbnailsViewModel : IDisposable
{
    private readonly DocumentTabViewModel _tab;
    private readonly DocumentSession _s;
    private readonly Action<Action> _post;
    private readonly TileCache<IImageHandle> _tiles;
    private readonly ThumbnailCache<IImageHandle> _thumbs;
    private readonly DocumentRenderer _renderer;
    private readonly TileScheduler _scheduler;
    private readonly HashSet<PageId> _visible = [];
    private bool _disposed;

    public ThumbnailsViewModel(DocumentTabViewModel tab, Func<byte[], int, int, IImageHandle> createImage, Action<Action>? post = null)
    {
        _tab = tab;
        _s = tab.Session ?? throw new ArgumentException("Tab must have a session.", nameof(tab));
        _post = post ?? (a => a());
        // 타일 캐시는 쓰지 않는다(DocumentRenderer가 요구해서 자리만 채움). 썸네일 예산은 ThumbnailCache 기본값.
        _tiles = new TileCache<IImageHandle>(1, i => i.Dispose());
        _thumbs = new ThumbnailCache<IImageHandle>(OnEvict);
        _renderer = new DocumentRenderer(_s, _tiles, _thumbs, createImage);
        _scheduler = new TileScheduler((r, ct) => _renderer.RenderAsync(r, ct), (r, data) => _post(() => OnReady(r, data)));
        _s.PagesChanged += OnPagesChanged;
        _tab.PropertyChanged += OnTabChanged;
        Rebuild();
    }

    public ObservableCollection<ThumbnailItem> Items { get; } = [];

    /// <summary>쪽 편집 명령(오른쪽 클릭 메뉴가 바인딩한다).</summary>
    public PageCommandsViewModel? Commands => _tab.PageCommands;

    /// <summary>현재 쪽이 바뀌었다. 패널이 그 칸을 보이는 위치로 스크롤한다.</summary>
    public event Action<int>? CurrentPageChanged;

    // ---- 보이는 칸 ----

    public void MarkVisible(PageId id)
    {
        if (_disposed) return;
        lock (_visible) { if (!_visible.Add(id)) return; }
        UpdateWanted();
    }

    public void MarkHidden(PageId id)
    {
        if (_disposed) return;
        lock (_visible) { if (!_visible.Remove(id)) return; }
        UpdateWanted();
    }

    // ---- 선택 ----

    /// <summary>목록에서 고른 칸들을 탭에 알린다. clicked가 있으면 그 쪽으로 이동한다.</summary>
    public void SetSelection(IReadOnlyList<ThumbnailItem> selected, ThumbnailItem? clicked)
    {
        _tab.SelectedPages = selected.Select(i => i.Id).ToHashSet();
        if (clicked is not null) _tab.CurrentPageIndex = Math.Max(0, clicked.PageNumber - 1);
    }

    // ---- 갱신 ----

    private void OnPagesChanged(PagesChangedEventArgs e) => _post(() => { if (!_disposed) Rebuild(); });

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DocumentTabViewModel.CurrentPageIndex)) return;
        UpdateCurrent();
        CurrentPageChanged?.Invoke(_tab.CurrentPageIndex);
    }

    private void UpdateCurrent()
    {
        int cur = _tab.CurrentPageIndex;
        for (int i = 0; i < Items.Count; i++) Items[i].IsCurrent = i == cur;
    }

    private void Rebuild()
    {
        var pages = _s.Pages;
        var existing = new Dictionary<PageId, ThumbnailItem>(Items.Count);
        foreach (var it in Items) existing[it.Id] = it;

        var target = new List<ThumbnailItem>(pages.Length);
        for (int i = 0; i < pages.Length; i++)
        {
            var p = pages[i];
            if (!existing.TryGetValue(p.Id, out var item)) item = new ThumbnailItem(p.Id);
            item.Session = _s;
            item.PageNumber = i + 1;
            var (w, h) = ThumbnailCache<IImageHandle>.SizeFor(p);
            item.DisplayWidth = w;
            item.DisplayHeight = h;
            if (item.Version != p.Version || item.AnnotRevision != p.AnnotRevision)
            {
                item.Version = p.Version;
                item.AnnotRevision = p.AnnotRevision;
                // 새 버전의 이미지가 이미 있으면 바로 쓰고, 없으면 옛 이미지를 둔 채 새로 그린다.
                if (_thumbs.TryGet(p.Id, p.Version, p.AnnotRevision, out var img)) item.Image = img;
            }
            target.Add(item);
        }

        // 목록을 target 모양으로 맞춘다. 같은 칸 객체는 유지(선택·이미지 보존).
        var keep = target.ToHashSet();
        for (int i = Items.Count - 1; i >= 0; i--)
            if (!keep.Contains(Items[i])) Items.RemoveAt(i);
        for (int i = 0; i < target.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], target[i])) continue;
            int at = Items.IndexOf(target[i]);
            if (at >= 0) Items.Move(at, i);
            else Items.Insert(i, target[i]);
        }

        UpdateCurrent();
        UpdateWanted();
    }

    private void UpdateWanted()
    {
        var pages = _s.Pages;
        var reqs = new List<TileRequest>();
        var byId = new Dictionary<PageId, PageInfo>(pages.Length);
        foreach (var p in pages) byId[p.Id] = p;
        foreach (var it in Items)
        {
            bool vis; lock (_visible) vis = _visible.Contains(it.Id);
            if (!vis || !byId.TryGetValue(it.Id, out var p)) continue;
            if (_thumbs.TryGet(p.Id, p.Version, p.AnnotRevision, out _)) continue;
            reqs.Add(DocumentRenderer.ThumbnailRequestFor(p));
        }
        _scheduler.SetWanted(reqs);
    }

    private void OnReady(TileRequest r, byte[] data)
    {
        if (_disposed) return;
        if (!_renderer.Accept(r, data)) return;   // 그 사이 쪽이 바뀌었으면 버린다
        foreach (var it in Items)
            if (it.Id == r.Key.Page && it.Version == r.Key.Version && it.AnnotRevision == r.Key.AnnotRevision && _thumbs.TryGet(r.Key.Page, r.Key.Version, r.Key.AnnotRevision, out var img))
                it.Image = img;
        UpdateWanted();
    }

    /// <summary>캐시가 이미지를 버릴 때: 화면이 쓰던 칸에서 먼저 떼고 해제한다(UI 스레드에서).</summary>
    private void OnEvict(IImageHandle img) => _post(() =>
    {
        foreach (var it in Items)
            if (ReferenceEquals(it.Image, img)) it.Image = null;
        img.Dispose();
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _s.PagesChanged -= OnPagesChanged;
        _tab.PropertyChanged -= OnTabChanged;
        _scheduler.Clear();
        _renderer.Dispose();
        foreach (var p in _s.Pages) _thumbs.RemovePage(p.Id);
        foreach (var it in Items) it.Image = null;
    }
}
