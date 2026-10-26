using System.Collections.Immutable;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.Document;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 문서 화면. ScrollViewer 안에 놓이며 MeasureOverride = PageLayout.Extent 이다. 보이는 부분만 그린다.
/// 페이지마다: 회색 바탕 → 썸네일(늘려서) → 이전 배율 타일(늘려서) → 현재 배율 타일.
/// UI 동작(스크롤·입력)은 직접 확인한 것만 보고서에 적는다. 계산은 PageLayout·ZoomMath·PrefetchPlanner가 맡는다.
/// </summary>
public sealed partial class DocumentView : Control
{
    public static readonly StyledProperty<DocumentTabViewModel?> TabProperty =
        AvaloniaProperty.Register<DocumentView, DocumentTabViewModel?>(nameof(Tab));

    private static readonly IBrush PlaceholderBrush = new SolidColorBrush(Color.Parse("#E6E6E6"));

    /// <summary>탭마다 따로 가지는 캐시·스케줄러·스크롤 위치.</summary>
    private sealed class ViewState : IDisposable
    {
        public required DocumentTabViewModel Tab;
        public required DocumentSession Session;
        public required TileCache<IImageHandle> Tiles;
        public required ThumbnailCache<IImageHandle> Thumbs;
        public required DocumentRenderer Renderer;
        public TileScheduler Scheduler = null!;
        public Vector Offset;
        public bool Disposed;

        // 덧그림용(UI 스레드 전용): 쪽 경계·회전 캐시, 검색 결과의 쪽별 묶음, 선택 사각형.
        public readonly Dictionary<(PageId, int), PageGeom> Geoms = new();
        public readonly HashSet<(PageId, int)> GeomPending = new();
        // 쪽 객체 목록(M4): (쪽, 버전)마다 한 번 전용 스레드에서 읽는다
        public readonly Dictionary<(PageId, int), PdfEditor.Document.Content.PageObjectIndex> ObjIndex = new();
        public readonly HashSet<(PageId, int)> ObjIndexPending = new();
        public Dictionary<PageId, List<SearchHit>>? HitsByPage;   // null = 다시 만들어야 함
        public (PageId Page, IReadOnlyList<System.Drawing.RectangleF> Rects)? SelRects;
        public bool SelBusy, SelDirty;
        public Action<PdfEditor.Document.Annotations.AnnotationsChangedEventArgs>? AnnotChangedHandler;

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (AnnotChangedHandler is { } h) Session.Annotations.Changed -= h;
            Scheduler.Clear();
            Renderer.Dispose();
            foreach (var p in Session.Pages) { Tiles.RemovePage(p.Id); Thumbs.RemovePage(p.Id); }
        }
    }

    /// <summary>쪽 경계(PDF 사용자 좌표)와 회전. PageTransform의 입력이다.</summary>
    private sealed record PageGeom(System.Drawing.RectangleF Bounds, int Rotation);

    private readonly Dictionary<DocumentTabViewModel, ViewState> _states = new();
    private ViewState? _state;
    private ScrollViewer? _sv;
    private PageLayout? _layout;
    private ImmutableArray<PageInfo> _layoutPages = [];
    private double _layoutViewportW = -1;
    private TopLevel? _top;
    private double _renderScaling = 1.0;   // Windows 화면 배율(125% = 1.25). 타일은 이만큼 더 촘촘히 그린다.
    private Point? _wheelAnchor;
    private bool _fromScroll;
    private double _navY = double.NaN;
    private Point _dragStartPos;
    private Vector _dragStartOffset;
    private bool _dragging;

    static DocumentView()
    {
        AffectsMeasure<DocumentView>(TabProperty);
    }

    public DocumentView()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.MediumQuality);
        LostFocus += (_, _) => RestoreSpaceTool();
    }

    public DocumentTabViewModel? Tab { get => GetValue(TabProperty); set => SetValue(TabProperty, value); }

    /// <summary>마지막으로 그린 프레임에서 뷰포트 안 페이지 면적 중 그림으로 덮인 비율(0~1). Task 18 벤치에서 읽는다.</summary>
    public double LastCoverage { get; private set; } = 1.0;

    /// <summary>벤치용: 마지막 프레임에서 현재 배율 타일로만 덮인 비율(0~1).</summary>
    public double LastExactCoverage { get; private set; } = 1.0;

    /// <summary>벤치용: 마지막 프레임에서 뷰포트와 겹친 쪽 수.</summary>
    public int LastVisiblePageCount { get; private set; }

    /// <summary>벤치용: Render가 불린 횟수(프레임 동기용).</summary>
    public long RenderCount { get; private set; }

    /// <summary>벤치용: 안쪽 스크롤 뷰어.</summary>
    internal ScrollViewer? Scroller => _sv;

    private Rect Viewport => _sv is null ? default : new Rect(_sv.Offset.X, _sv.Offset.Y, _sv.Viewport.Width, _sv.Viewport.Height);

    // ---- 연결 ----

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _sv = this.FindAncestorOfType<ScrollViewer>();
        if (_sv is not null) _sv.ScrollChanged += OnScrollChanged;
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null) { _top.ScalingChanged += OnScalingChanged; _renderScaling = ValidScaling(_top.RenderScaling); }
        SwitchTab(Tab);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_top is not null) { _top.ScalingChanged -= OnScalingChanged; _top = null; }
        if (_sv is not null) _sv.ScrollChanged -= OnScrollChanged;
        SwitchTab(null);
        foreach (var s in _states.Values.ToList()) DisposeState(s);
        _sv = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TabProperty && _sv is not null) SwitchTab((DocumentTabViewModel?)change.NewValue);
    }

    private void SwitchTab(DocumentTabViewModel? tab)
    {
        CancelGesture();
        CloseStickyEditor(commit: true);
        CloseFreeTextEditor(commit: true);
        CloseLineEditor(commit: true);
        CloseParagraphEditor();
        if (_state is not null)
        {
            if (_sv is not null) _state.Offset = _sv.Offset;
            _state.Tab.PropertyChanged -= OnTabPropertyChanged;
            Hook(_state, false);
            _state.Scheduler.Clear();
        }
        _state = null;
        _layout = null;
        if (tab?.Session is { } session)
        {
            if (!_states.TryGetValue(tab, out var st))
            {
                st = CreateState(tab, session);
                _states[tab] = st;
                var captured = st;
                tab.Detached += () => DisposeState(captured);
            }
            _state = st;
            tab.PropertyChanged += OnTabPropertyChanged;
            Hook(st, true);
            Cursor = CursorFor(tab.Tool);
            RebuildLayout();
            InvalidateMeasure();
            if (_sv is not null)
            {
                tab.ViewportSize = _sv.Viewport;
                _sv.UpdateLayout();
                _sv.Offset = st.Offset;
            }
        }
        InvalidateMeasure();
        InvalidateVisual();
        UpdateWanted();
    }

    private ViewState CreateState(DocumentTabViewModel tab, DocumentSession session)
    {
        static void DisposeOnUi(IImageHandle i) => Dispatcher.UIThread.Post(i.Dispose);
        var tiles = new TileCache<IImageHandle>(TileCache<IImageHandle>.DefaultBudgetBytes, DisposeOnUi);
        var thumbs = new ThumbnailCache<IImageHandle>(DisposeOnUi);
        var renderer = new DocumentRenderer(session, tiles, thumbs, (data, w, h) => new BitmapHandle(data, w, h));
        var st = new ViewState { Tab = tab, Session = session, Tiles = tiles, Thumbs = thumbs, Renderer = renderer };
        st.Scheduler = new TileScheduler(
            (r, ct) => renderer.RenderAsync(r, ct),
            // onReady는 스레드 풀 스레드에서 불린다 → UI 스레드로 넘긴다.
            (r, data) => Dispatcher.UIThread.Post(() => OnTileReady(st, r, data)));
        // PagesChanged(전용 스레드)는 UI 스레드가 아니므로 디스패치한다.
        renderer.Invalidated += () => Dispatcher.UIThread.Post(() => OnPagesInvalidated(st));
        // 주석 모델이 바뀌면(전용 스레드) 주석 층을 다시 그린다.
        st.AnnotChangedHandler = _ => Dispatcher.UIThread.Post(() =>
        {
            if (!st.Disposed && ReferenceEquals(st, _state)) InvalidateVisual();
        });
        session.Annotations.Changed += st.AnnotChangedHandler;
        return st;
    }

    private void DisposeState(ViewState st)
    {
        if (_editState is not null && ReferenceEquals(_editState, st)) CloseStickyEditor(commit: false);
        if (_ftState is not null && ReferenceEquals(_ftState, st)) CloseFreeTextEditor(commit: false);
        CloseLineEditor(commit: false, onlyFor: st);
        if (ReferenceEquals(_paraSt, st)) CloseParagraphEditor();
        if (ReferenceEquals(_state, st))
        {
            st.Tab.PropertyChanged -= OnTabPropertyChanged;
            Hook(st, false);
            _state = null;
            _layout = null;
            InvalidateMeasure();
            InvalidateVisual();
        }
        _states.Remove(st.Tab);
        st.Dispose();
    }

    // ---- 모델 변화 ----

    private void OnTileReady(ViewState st, TileRequest r, byte[] data)
    {
        if (st.Disposed) return;
        if (st.Renderer.Accept(r, data) && ReferenceEquals(st, _state))
        {
            InvalidateVisual();
            UpdateWanted();
        }
    }

    private void OnPagesInvalidated(ViewState st)
    {
        if (st.Disposed || !ReferenceEquals(st, _state)) return;
        RebuildLayout();
        InvalidateMeasure();
        InvalidateVisual();
        UpdateWanted();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_state is null || _sv is null) return;
        switch (e.PropertyName)
        {
            case nameof(DocumentTabViewModel.Scale): OnScaleChanged(); Dispatcher.UIThread.Post(OnParagraphViewChanged, DispatcherPriority.Loaded); break;
            case nameof(DocumentTabViewModel.ViewMode): OnViewModeChanged(); break;
            case nameof(DocumentTabViewModel.CurrentPageIndex):
                if (!_fromScroll) GoToPage(_state.Tab.CurrentPageIndex);
                break;
            case nameof(DocumentTabViewModel.PageCount): OnPagesInvalidated(_state); break;
            case nameof(DocumentTabViewModel.Tool): Cursor = CursorFor(_state.Tab.Tool); CancelGesture(); _hover = null; if (_state.Tab.Tool != ToolKind.EditText) CloseParagraphEditor(); InvalidateVisual(); break;
        }
    }

    private static double ValidScaling(double s) => s > 0 && double.IsFinite(s) ? s : 1.0;

    // 다른 배율의 모니터로 옮기거나 Windows 배율을 바꾸면 새 해상도로 다시 그린다(타일 키에 해상도가 들어 있어 캐시가 섞이지 않는다).
    private void OnScalingChanged(object? sender, EventArgs e)
    {
        if (_top is null) return;
        _renderScaling = ValidScaling(_top.RenderScaling);
        InvalidateVisual();
        UpdateWanted();
    }

    private void RebuildLayout()
    {
        if (_state is null) { _layout = null; return; }
        var pages = _state.Session.Pages;
        _layoutPages = pages;
        _layoutViewportW = _sv?.Viewport.Width ?? 0;
        _layout = PageLayout.Build(pages, ZoomMath.DipPerPoint(_state.Tab.Scale), _layoutViewportW, _state.Tab.ViewMode,
            Math.Clamp(_state.Tab.CurrentPageIndex, 0, Math.Max(0, pages.Length - 1)));
    }

    private void OnScaleChanged()
    {
        var old = _layout;
        RebuildLayout();
        InvalidateMeasure();
        if (old is not null && _layout is not null && _sv is not null)
        {
            var viewport = _sv.Viewport;
            var anchor = _wheelAnchor ?? new Point(viewport.Width / 2, viewport.Height / 2);
            var target = ZoomMath.AnchorOffset(old, _layout, _sv.Offset, anchor, viewport);
            _sv.UpdateLayout();   // 새 Extent를 먼저 반영해야 Offset이 잘리지 않는다 (검증 필요: 실제 화면)
            _sv.Offset = target;
        }
        InvalidateVisual();
        UpdateWanted();
    }

    private void OnViewModeChanged()
    {
        RebuildLayout();
        InvalidateMeasure();
        if (_sv is null || _state is null) return;
        _sv.UpdateLayout();
        GoToPage(_state.Tab.CurrentPageIndex);
        InvalidateVisual();
        UpdateWanted();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_state is null || _sv is null) return;
        _state.Tab.ViewportSize = _sv.Viewport;
        if (Math.Abs(_sv.Viewport.Width - _layoutViewportW) > 0.5)
        {
            RebuildLayout();
            InvalidateMeasure();
        }
        // 이어서 보기: 스크롤 위치로 현재 페이지를 따라간다. 방금 쪽 이동한 직후의 위치는 건너뛴다.
        if (_layout is { Mode: ViewMode.Continuous } lay && !(Math.Abs(_sv.Offset.Y - _navY) < 1))
        {
            int cur = lay.CurrentPage(Viewport);
            if (cur != _state.Tab.CurrentPageIndex)
            {
                _fromScroll = true;
                try { _state.Tab.CurrentPageIndex = cur; } finally { _fromScroll = false; }
            }
            _navY = double.NaN;
        }
        InvalidateVisual();
        UpdateWanted();
    }

    // ---- 쪽 이동 ----

    public void GoToPage(int index)
    {
        if (_state is null || _sv is null || _layout is null) return;
        var pages = _state.Session.Pages;
        if (pages.Length == 0) return;
        index = Math.Clamp(index, 0, pages.Length - 1);
        if (_layout.Mode == ViewMode.SinglePage)
        {
            RebuildLayout();
            InvalidateMeasure();
            _sv.UpdateLayout();
            _sv.Offset = new Vector(_sv.Offset.X, 0);
            InvalidateVisual();
            UpdateWanted();
            return;
        }
        var r = _layout.GetPageRect(index);
        double y = Math.Max(0, r.Y - PageLayout.MarginPx);
        _navY = Math.Min(y, Math.Max(0, _layout.Extent.Height - _sv.Viewport.Height));
        _sv.Offset = new Vector(_sv.Offset.X, y);
    }

    // ---- 그리기 ----

    protected override Size MeasureOverride(Size availableSize) => _layout?.Extent ?? default;

    public override void Render(DrawingContext ctx)
    {
        RenderCount++;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));   // 빈 곳도 포인터를 받도록
        var st = _state; var lay = _layout;
        if (st is null || lay is null || _sv is null) { LastCoverage = 1.0; LastExactCoverage = 1.0; LastVisiblePageCount = 0; return; }
        var vp = Viewport;
        var pages = _layoutPages;
        var painted = new List<Rect>();
        var exact = new List<Rect>();
        var pageRects = new List<Rect>();
        double scale = lay.Scale;   // 1pt당 DIP
        double rs = _renderScaling;
        int scaleKey = TileKey.ToScaleKey(TileGrid.PixelScale(scale, rs));

        foreach (int i in lay.PagesIntersecting(vp))
        {
            var info = pages[i];
            var pr = lay.GetPageRect(i);
            pageRects.Add(pr);
            if (IsAnnotationTool(st.Tab.Tool) || st.Tab.Tool == ToolKind.Snapshot) TryGetGeom(st, info, out _);   // 첫 클릭부터 쪽 정보가 있도록 미리 읽는다
            if (IsObjectTool(st.Tab.Tool) || (st.Tab.Tool == ToolKind.AnnotSelect && st.Session.Content.HasEdits)) { TryGetGeom(st, info, out _); TryGetObjectIndex(st, info, out _); }
            using (ctx.PushClip(pr))
            {
                ctx.FillRectangle(PlaceholderBrush, pr);
                if (st.Thumbs.TryGetOrOlder(info.Id, info.Version, info.AnnotRevision, out var th) && th is BitmapHandle tb)
                {
                    ctx.DrawImage(tb.Bitmap, new Rect(0, 0, tb.Width, tb.Height), pr);
                    painted.Add(pr);
                }
                // 저해상도 미리보기(첫 화면용): 타일이 오기 전에 보이는 영역을 먼저 덮는다. 대체 그림·현재 타일이 그 위에 덮어 그려진다.
                foreach (var (key, img) in st.Tiles.PreviewsFor(info.Id, info.Version))
                    if (img is BitmapHandle pb)
                    {
                        var dest = PreviewPlan.DestRect(key, pb.Width, pb.Height, pr, scale);
                        ctx.DrawImage(pb.Bitmap, new Rect(0, 0, pb.Width, pb.Height), dest);
                        painted.Add(dest.Intersect(pr));
                    }
                foreach (var (key, img) in st.Tiles.FallbackFor(info.Id, info.Version, info.AnnotRevision, scaleKey))
                    if (img is BitmapHandle fb)
                    {
                        var dest = TileDest(info, key, pr, scale, rs);
                        ctx.DrawImage(fb.Bitmap, new Rect(0, 0, fb.Width, fb.Height), dest);
                        painted.Add(dest.Intersect(pr));
                    }
                foreach (var key in TileGrid.Tiles(info, scale, pr, vp, 0, rs))
                    if (st.Tiles.TryGet(key, out var img) && img is BitmapHandle cb)
                    {
                        var dest = TileDest(info, key, pr, scale, rs);
                        ctx.DrawImage(cb.Bitmap, new Rect(0, 0, cb.Width, cb.Height), dest);
                        painted.Add(dest.Intersect(pr));
                        exact.Add(dest.Intersect(pr));
                    }
                DrawAnnotationLayer(ctx, st, info, pr, scale);   // ④ 주석 층
                DrawSelection(ctx, st, info, pr, scale);
                DrawObjectSelection(ctx, st, info, pr, scale);
            }
            DrawOverlays(ctx, st, info, pr, scale);   // ⑤ 검색 강조·텍스트 선택
        }
        DrawEraserCursor(ctx);
        DrawSnapshotOverlay(ctx);
        LastCoverage = ViewportCoverage.Compute(vp, pageRects, painted);
        LastExactCoverage = ViewportCoverage.Compute(vp, pageRects, exact);
        LastVisiblePageCount = pageRects.Count;
    }

    // ---- 주석 층 ----

    private static readonly IReadOnlyDictionary<PdfEditor.Document.Annotations.AnnotId, PdfEditor.Document.Annotations.Annotation?> NoPreviews
        = new Dictionary<PdfEditor.Document.Annotations.AnnotId, PdfEditor.Document.Annotations.Annotation?>();

    private (PageId Page, IReadOnlyDictionary<PdfEditor.Document.Annotations.AnnotId, PdfEditor.Document.Annotations.Annotation?> Items)? _annotPreview;

    /// <summary>
    /// 만드는 중이거나 편집 중인 주석의 미리보기(한 쪽 대상). 값 null = 그 주석을 그리지 않음(예: 지우개로 다 지우는 중).
    /// UI 스레드에서 지정한다. null이면 미리보기 없음.
    /// </summary>
    public void SetAnnotationPreview(PageId? page, IReadOnlyDictionary<PdfEditor.Document.Annotations.AnnotId, PdfEditor.Document.Annotations.Annotation?>? items)
    {
        _annotPreview = page is { } p && items is { Count: > 0 } ? (p, items) : null;
        InvalidateVisual();
    }

    private void DrawAnnotationLayer(DrawingContext ctx, ViewState st, PageInfo info, Rect pr, double scale)
    {
        var annots = st.Session.Annotations.Get(info.Id);
        var previews = _annotPreview is { } ap && ap.Page == info.Id ? ap.Items : NoPreviews;
        if (previews.Count == 0 && !annots.Items.Any(i => i.ModelDrawn)) return;
        if (!TryGetGeom(st, info, out var g)) return;   // 도착하면 다시 그린다
        var tr = new Pdf.Native.PageTransform(g.Bounds, g.Rotation, scale);
        long t0 = OverlayDrawTimed is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        Annotations.AnnotationOverlay.DrawPage(ctx, annots, pr, tr, g.Rotation, previews);
        if (OverlayDrawTimed is { } cb) cb(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }

    /// <summary>벤치용: 주석 층을 실제로 그린 쪽마다 걸린 시간(ms)을 알린다. 지정하지 않으면 시간을 재지 않는다.</summary>
    public static Action<double>? OverlayDrawTimed { get; set; }

    // ---- 덧그림: 검색 강조·텍스트 선택 ----

    private static readonly IBrush HitBrush = new SolidColorBrush(Color.FromArgb(0x78, 0xFF, 0xE0, 0x30));
    private static readonly IBrush CurrentHitBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0x8C, 0x00));
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x5F, 0xB8));

    private SearchHit? _revealPending;
    private static readonly Pen MarqueePen = new(new SolidColorBrush(Color.FromRgb(0x00, 0x5F, 0xB8)), 1, new DashStyle([3, 3], 0));
    private bool _rectSelecting;   // Alt+끌기 사각형 선택 중
    private bool _selecting;
    private int _selPageIndex = -1;
    private readonly List<(int Page, Point InPage, bool Begin)> _charQueue = new();
    private bool _charBusy;

    /// <summary>현재 상태의 검색·선택 이벤트를 붙이거나 뗀다. 한 번에 현재 상태 하나만 붙어 있다.</summary>
    private void Hook(ViewState st, bool on)
    {
        if (st.Tab.Search is { } s)
        {
            if (on) { s.PropertyChanged += OnSearchPropertyChanged; s.Results.CollectionChanged += OnSearchResultsChanged; }
            else { s.PropertyChanged -= OnSearchPropertyChanged; s.Results.CollectionChanged -= OnSearchResultsChanged; }
        }
        if (on) { st.Tab.TextSelection.Changed += OnSelectionChanged; st.Tab.Selection.Changed += OnAnnotSelectionChanged; st.Tab.AnnotationPreviewRequested += SetAnnotationPreview; st.Tab.RevealRequested += OnRevealRequested; }
        else { st.Tab.TextSelection.Changed -= OnSelectionChanged; st.Tab.Selection.Changed -= OnAnnotSelectionChanged; st.Tab.AnnotationPreviewRequested -= SetAnnotationPreview; st.Tab.RevealRequested -= OnRevealRequested; }
        if (on) { st.HitsByPage = null; RefreshSelection(st); }
    }

    private void OnSearchResultsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_state is null) return;
        _state.HitsByPage = null;
        InvalidateVisual();
    }

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_state is null || e.PropertyName != nameof(SearchViewModel.CurrentHit)) return;
        _revealPending = _state.Tab.Search?.CurrentHit;
        TryReveal();
        InvalidateVisual();
    }

    private void OnAnnotSelectionChanged() => InvalidateVisual();

    // 코멘트 목록에서 주석을 눌렀을 때: 그 사각형을 검색 결과처럼 화면 안으로 스크롤한다 (쪽 정보가 아직 없으면 도착한 뒤 다시).
    private void OnRevealRequested(PageId page, Pdf.Native.PdfRectF r)
    {
        _revealPending = new SearchHit(page, 0, 0, 0, "", [new System.Drawing.RectangleF(r.Left, r.Bottom, r.Right - r.Left, r.Top - r.Bottom)]);
        TryReveal();
    }

    private void OnSelectionChanged()
    {
        if (_state is null) return;
        if (_state.Tab.TextSelection.Page is null) _state.SelRects = null;
        RefreshSelection(_state);
        InvalidateVisual();
    }

    private bool TryGetGeom(ViewState st, PageInfo info, out PageGeom g)
    {
        var key = (info.Id, info.Version);
        if (st.Geoms.TryGetValue(key, out g!)) return true;
        if (st.GeomPending.Add(key)) _ = LoadGeomAsync(st, key);
        return false;
    }

    // 전용 스레드에서 쪽 경계·회전을 한 번 읽는다. 핸들은 그 안에서만 쓴다.
    private async Task LoadGeomAsync(ViewState st, (PageId Id, int Version) key)
    {
        PageGeom? geom = null;
        try
        {
            geom = await st.Session.Thread.InvokeAsync(() =>
            {
                if (st.Session.IndexOf(key.Id) < 0) return null;
                var p = st.Session.GetLoadedPage(key.Id);
                return new PageGeom(p.BoundsPt, p.Rotation);
            });
        }
        catch (Exception) { }   // PDFium 스레드가 닫혔거나 쪽이 없어짐: 덧그림만 생략한다.
        Dispatcher.UIThread.Post(() =>
        {
            st.GeomPending.Remove(key);
            if (st.Disposed || geom is null) return;
            st.Geoms[key] = geom;
            if (!ReferenceEquals(st, _state)) return;
            RetryPendingPress();
            TryReveal();
            InvalidateVisual();
        });
    }

    /// <summary>쪽 안의 화면 점(쪽 왼쪽 위 기준)을 쪽 좌표(pt)로 바꾼다. 쪽 정보가 아직 없으면 null.</summary>
    private System.Drawing.PointF? TryPagePoint(int pageIndex, Point inPage)
    {
        if (_state is not { } st || _layout is not { } lay || pageIndex < 0 || pageIndex >= _layoutPages.Length) return null;
        if (!TryGetGeom(st, _layoutPages[pageIndex], out var g)) return null;
        var tr = new Pdf.Native.PageTransform(g.Bounds, g.Rotation, lay.Scale);
        var (x, y) = tr.ToPage(new Pdf.Native.ViewPoint(inPage.X, inPage.Y));
        return new System.Drawing.PointF((float)x, (float)y);
    }

    private static Rect ToDoc(Pdf.Native.PageTransform tr, System.Drawing.RectangleF r, Rect pageRect)
    {
        var v = tr.ToView(r);
        return new Rect(pageRect.X + v.X, pageRect.Y + v.Y, Math.Max(v.W, 1), Math.Max(v.H, 1));
    }

    private void DrawOverlays(DrawingContext ctx, ViewState st, PageInfo info, Rect pr, double scale)
    {
        var search = st.Tab.Search;
        bool hasHits = search is { Results.Count: > 0 };
        bool hasSel = st.SelRects is { } sr && sr.Page == info.Id;
        var tsel = st.Tab.TextSelection;
        bool hasMarquee = _rectSelecting && tsel.RectPt is not null && tsel.Page == info.Id;   // 끄는 동안만 점선 테두리를 보인다
        if (!hasHits && !hasSel && !hasMarquee) return;
        if (hasHits && st.HitsByPage is null)
            st.HitsByPage = search!.Results.GroupBy(h => h.Page).ToDictionary(g => g.Key, g => g.ToList());
        List<SearchHit>? hits = null;
        bool pageHasHits = hasHits && st.HitsByPage!.TryGetValue(info.Id, out hits);
        if (!pageHasHits && !hasSel && !hasMarquee) return;
        if (!TryGetGeom(st, info, out var g)) return;   // 도착하면 다시 그린다

        var tr = new Pdf.Native.PageTransform(g.Bounds, g.Rotation, scale);
        using (ctx.PushClip(pr))
        {
            if (pageHasHits)
                foreach (var h in hits!)
                {
                    var brush = ReferenceEquals(h, search!.CurrentHit) ? CurrentHitBrush : HitBrush;
                    foreach (var r in h.RectsPt) ctx.FillRectangle(brush, ToDoc(tr, r, pr));
                }
            if (hasSel)
                foreach (var r in st.SelRects!.Value.Rects) ctx.FillRectangle(SelectionBrush, ToDoc(tr, r, pr));
            if (hasMarquee)
            {
                // 사각형 선택 테두리: 점선. 쪽 좌표에서 매번 변환하므로 확대·스크롤에도 같은 자리에 남는다.
                var mr = ToDoc(tr, tsel.RectPt!.Value, pr);
                ctx.DrawRectangle(null, MarqueePen, mr);
            }
        }
    }

    /// <summary>현재 검색 결과가 보이지 않으면 화면 안으로 스크롤한다. 쪽 정보가 아직 없으면 도착한 뒤 다시 부른다.</summary>
    private void TryReveal()
    {
        var st = _state; var lay = _layout; var hit = _revealPending;
        if (st is null || lay is null || _sv is null || hit is null || hit.RectsPt.Count == 0) return;
        int idx = -1;
        for (int i = 0; i < _layoutPages.Length; i++) if (_layoutPages[i].Id == hit.Page) { idx = i; break; }
        if (idx < 0) { _revealPending = null; return; }
        if (!TryGetGeom(st, _layoutPages[idx], out var g)) return;
        _revealPending = null;

        var tr = new Pdf.Native.PageTransform(g.Bounds, g.Rotation, lay.Scale);
        var rect = ToDoc(tr, hit.RectsPt[0], lay.GetPageRect(idx));
        var vp = Viewport;
        if (vp.Width <= 0 || vp.Height <= 0 || vp.Contains(rect.TopLeft) && vp.Contains(rect.BottomRight)) return;
        double x = rect.Right <= vp.Right && rect.X >= vp.X ? _sv.Offset.X : rect.Center.X - vp.Width / 2;
        _sv.Offset = ClampOffset(x, rect.Y - vp.Height / 3);
    }

    // 선택한 글자 범위의 사각형을 전용 스레드에서 읽는다. 끄는 동안 요청이 겹치면 마지막 것만 다시 한다.
    private async void RefreshSelection(ViewState st)
    {
        if (st.SelBusy) { st.SelDirty = true; return; }
        st.SelBusy = true;
        try
        {
            do
            {
                st.SelDirty = false;
                var sel = st.Tab.TextSelection;
                if (sel.Page is { } rpage && sel.RectPt is { } rectPt)
                {
                    var session = st.Session;
                    var rects = await session.Thread.InvokeAsync(() => RectTextSelector.Resolve(session, rpage, rectPt).Rects);
                    if (st.Disposed) return;
                    st.SelRects = (rpage, rects);
                }
                else if (sel.Page is not { } page || sel.Range is not { } r) { st.SelRects = null; }
                else
                {
                    var session = st.Session;
                    var rects = await session.Thread.InvokeAsync(() => session.IndexOf(page) < 0
                        ? Array.Empty<System.Drawing.RectangleF>()
                        : session.GetLoadedText(page).GetRects(r.Start, r.Count));
                    if (st.Disposed) return;
                    st.SelRects = (page, rects);
                }
                if (ReferenceEquals(st, _state)) InvalidateVisual();
            } while (st.SelDirty);
        }
        catch (Exception) { }   // PDFium 스레드가 닫혔다: 선택 표시만 생략한다.
        finally { st.SelBusy = false; }
    }

    // 끌기: 눌린 점과 움직인 점의 글자 번호를 차례로 구한다. 쪽마다 전용 스레드 작업 한 번, 움직임은 마지막 것만 남긴다.
    private void QueueCharQuery(int pageIndex, Point inPage, bool begin)
    {
        if (!begin && _charQueue.Count > 0 && !_charQueue[^1].Begin) _charQueue[^1] = (pageIndex, inPage, false);
        else _charQueue.Add((pageIndex, inPage, begin));
        if (!_charBusy) _ = RunCharQueueAsync();
    }

    private async Task RunCharQueueAsync()
    {
        _charBusy = true;
        try
        {
            while (_charQueue.Count > 0)
            {
                var q = _charQueue[0];
                _charQueue.RemoveAt(0);
                var st = _state; var lay = _layout;
                if (st is null || lay is null || !_selecting || q.Page >= _layoutPages.Length) continue;
                var id = _layoutPages[q.Page].Id;
                double scale = lay.Scale;
                var session = st.Session;
                int idx = await session.Thread.InvokeAsync(() =>
                {
                    if (session.IndexOf(id) < 0) return -1;
                    // 쪽 핸들은 경계·회전을 읽는 데만 쓰고 놓는다(다음 GetLoaded*가 닫을 수 있다).
                    var page = session.GetLoadedPage(id);
                    var tr = new Pdf.Native.PageTransform(page.BoundsPt, page.Rotation, scale);
                    var (x, y) = tr.ToPage(new Pdf.Native.ViewPoint(q.InPage.X, q.InPage.Y));
                    return session.GetLoadedText(id).GetCharIndexAtPos(x, y, 4.0 / scale);
                });
                if (st.Disposed || !_selecting || idx < 0) continue;
                var sel = st.Tab.TextSelection;
                if (q.Begin || sel.Page is null) sel.Begin(id, idx);
                else if (sel.Page == id) sel.Extend(idx);
            }
        }
        catch (Exception) { _charQueue.Clear(); }   // PDFium 스레드가 닫혔다
        finally { _charBusy = false; }
    }

    private sealed class AvaloniaClipboard(IClipboard clipboard) : IClipboardService
    {
        public Task SetTextAsync(string text) => clipboard.SetTextAsync(text);
    }

    private async void CopySelection()
    {
        var st = _state;
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (st is null || cb is null) return;
        try
        {
            var r = await TextCopier.CopyAsync(st.Session, st.Tab.TextSelection, new AvaloniaClipboard(cb));
            if (r == CopyResult.Restricted) st.Tab.RaiseNotice(Strings.Msg_CopyRestricted);
        }
        catch (Exception) { }   // 클립보드를 쓸 수 없는 경우(다른 프로그램이 잡고 있음 등): 조용히 넘어간다.
    }

    private static Rect TileDest(PageInfo info, TileKey key, Rect pageRect, double scale, double renderScaling) =>
        TileGrid.DestRect(info, key, pageRect, scale, renderScaling);

    private void UpdateWanted()
    {
        var st = _state; var lay = _layout;
        if (st is null || lay is null || _sv is null) return;
        // 미리보기가 필요 없는 경우: 이미 덮는 미리보기가 있거나, 다른 배율 타일이 대체 그림으로 있을 때(확대·축소 직후).
        bool PreviewCovered(PageInfo page, Rect regionPt)
        {
            int sk = TileKey.ToScaleKey(TileGrid.PixelScale(lay.Scale, _renderScaling));
            if (st.Tiles.FallbackFor(page.Id, page.Version, page.AnnotRevision, sk).Count > 0) return true;
            foreach (var (key, img) in st.Tiles.PreviewsFor(page.Id, page.Version))
                if (key.AnnotRevision == page.AnnotRevision && PreviewPlan.Covers(PreviewPlan.CoveredPt(key, img.Width, img.Height), key.Scale, regionPt)) return true;
            return false;
        }
        var plan = PrefetchPlanner.Plan(_layoutPages, lay, Viewport, lay.Scale,
            k => st.Tiles.TryGet(k, out _), (id, v, ar) => st.Thumbs.TryGet(id, v, ar, out _), _renderScaling, PreviewCovered);
        st.Scheduler.SetWanted(plan);
    }

    // ---- 입력 ----

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_state is null || _sv is null || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            base.OnPointerWheelChanged(e);   // 일반 휠·Shift+휠은 ScrollViewer가 처리
            return;
        }
        var tab = _state.Tab;
        _wheelAnchor = e.GetPosition(_sv);
        try { tab.Scale = ZoomMath.Step(tab.Scale, e.Delta.Y > 0 ? 1 : -1); }
        finally { _wheelAnchor = null; }
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_state is null || _sv is null) return;
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed && UsesTextMenu(_state.Tab.Tool))
        {
            ShowTextMenu(e);
            e.Handled = true;
            return;
        }
        if (HandleSnapshotPressed(e)) return;
        if (HandleObjectPointerPressed(e)) return;
        if (HandleAnnotPointerPressed(e)) return;
        if (_state.Tab.Tool == ToolKind.Hand && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            _dragStartPos = e.GetPosition(_sv);
            _dragStartOffset = _sv.Offset;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        else if (UsesTextDrag(_state.Tab.Tool) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // 텍스트 선택: 새로 누르면 이전 선택을 지우고, 누른 점의 글자부터 시작한다(글자가 없으면 선택 없음).
            _state.Tab.TextSelection.Clear();
            var hit = _layout?.HitTest(e.GetPosition(this));
            if (hit is { } h)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && _state.Tab.Tool == ToolKind.TextSelect
                    && TryPagePoint(h.Page, h.InPage) is { } startPt)
                {
                    // Alt+끌기: 사각형 선택. 시작 점을 쪽 좌표로 저장한다.
                    _rectSelecting = true;
                    _selPageIndex = h.Page;
                    _state.Tab.TextSelection.BeginRect(_layoutPages[h.Page].Id, startPt);
                }
                else
                {
                    _selecting = true;
                    _selPageIndex = h.Page;
                    QueueCharQuery(h.Page, h.InPage, begin: true);
                }
                e.Pointer.Capture(this);
            }
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_state is null || _sv is null) return;
        if (HandleSnapshotMoved(e)) return;
        if (HandleObjectPointerMoved(e)) return;
        HandleAnnotPointerMoved(e);
        if (_selecting && _layout is { } lay && _selPageIndex >= 0 && _selPageIndex < _layoutPages.Length)
        {
            var origin = lay.GetPageRect(_selPageIndex).TopLeft;
            var p = e.GetPosition(this);
            QueueCharQuery(_selPageIndex, new Point(p.X - origin.X, p.Y - origin.Y), begin: false);
        }
        if (_rectSelecting && _layout is { } rlay && _selPageIndex >= 0 && _selPageIndex < _layoutPages.Length)
        {
            var origin = rlay.GetPageRect(_selPageIndex).TopLeft;
            var p = e.GetPosition(this);
            if (TryPagePoint(_selPageIndex, new Point(p.X - origin.X, p.Y - origin.Y)) is { } pt)
                _state.Tab.TextSelection.ExtendRect(pt);
        }
        if (_dragging)
        {
            var d = e.GetPosition(_sv) - _dragStartPos;
            _sv.Offset = ClampOffset(_dragStartOffset.X - d.X, _dragStartOffset.Y - d.Y);
            return;
        }
        var hit = _layout?.HitTest(e.GetPosition(this));
        _state.Tab.SetCursorFromPagePoint(hit?.InPage, _layout?.Scale ?? 1);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (HandleSnapshotReleased(e)) return;
        if (HandleObjectPointerReleased(e)) return;
        if (HandleAnnotPointerReleased(e)) return;
        if (_dragging) { _dragging = false; e.Pointer.Capture(null); }
        if (_rectSelecting)
        {
            _rectSelecting = false;
            _selPageIndex = -1;
            e.Pointer.Capture(null);
            // 클릭만 한 경우(사각형이 거의 없음)는 선택을 지운다.
            if (_state?.Tab.TextSelection.RectPt is { } rc && rc.Width < 2 && rc.Height < 2) _state.Tab.TextSelection.Clear();
            InvalidateVisual();   // 점선 테두리를 지우고 하이라이트만 남긴다
        }
        if (_selecting)
        {
            e.Pointer.Capture(null);
            _selPageIndex = -1;
            if (_state is { } mst && IsTextMarkupTool(mst.Tab.Tool)) _ = FinishMarkupAsync(mst, mst.Tab.Tool);
            else _selecting = false;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _state?.Tab.SetCursorFromPagePoint(null, 1);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (HandleObjectKey(e)) return;
        if (HandleAnnotKey(e)) return;
        if (e is { Key: Key.C, KeyModifiers: KeyModifiers.Control })
        {
            CopySelection();
            e.Handled = true;
            return;
        }
        if (e is { Key: Key.A, KeyModifiers: KeyModifiers.Control } && _state is { } ast && UsesTextMenu(ast.Tab.Tool))
        {
            SelectAllFromKeyboard();
            e.Handled = true;
            return;
        }
        if (_state is null || _sv is null || _layout is null || e.KeyModifiers != KeyModifiers.None) return;
        var tab = _state.Tab;
        bool single = _layout.Mode == ViewMode.SinglePage;
        switch (e.Key)
        {
            case Key.Space:
                // 스페이스바를 누르고 있는 동안 손(이동) 도구, 떼면 원래 도구(2026-10-20 사용자 요청)
                if (_spacePrevTool is null && tab.Tool != ToolKind.Hand && !IsLineEditorOpen && !IsFreeTextEditorOpen)
                {
                    _spacePrevTool = tab.Tool;
                    tab.Tool = ToolKind.Hand;
                }
                break;
            case Key.H: tab.Tool = ToolKind.Hand; break;
            case Key.V: tab.Tool = ToolKind.TextSelect; break;
            case Key.Left when !(_sv.Extent.Width > _sv.Viewport.Width): tab.PrevPage(); GoToPage(tab.CurrentPageIndex); break;
            case Key.Right when !(_sv.Extent.Width > _sv.Viewport.Width): tab.NextPage(); GoToPage(tab.CurrentPageIndex); break;
            case Key.PageDown:
                if (single) tab.NextPage();
                else _sv.Offset = ClampOffset(_sv.Offset.X, _sv.Offset.Y + _sv.Viewport.Height * 0.9);
                break;
            case Key.PageUp:
                if (single) tab.PrevPage();
                else _sv.Offset = ClampOffset(_sv.Offset.X, _sv.Offset.Y - _sv.Viewport.Height * 0.9);
                break;
            case Key.Home: tab.FirstPage(); GoToPage(tab.CurrentPageIndex); break;
            case Key.End: tab.LastPage(); GoToPage(tab.CurrentPageIndex); break;
            default: return;
        }
        e.Handled = true;
    }

    private ToolKind? _spacePrevTool;

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) RestoreSpaceTool();
    }

    private void RestoreSpaceTool()
    {
        if (_spacePrevTool is { } prev && _state is { } st)
        {
            if (st.Tab.Tool == ToolKind.Hand) st.Tab.Tool = prev;
        }
        _spacePrevTool = null;
    }

    private Vector ClampOffset(double x, double y) =>
        new(Math.Clamp(x, 0, Math.Max(0, _sv!.Extent.Width - _sv.Viewport.Width)),
            Math.Clamp(y, 0, Math.Max(0, _sv.Extent.Height - _sv.Viewport.Height)));
}
