using System.Collections.Immutable;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfEditor.App.Documents;
using PdfEditor.Document;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 문서 화면. ScrollViewer 안에 놓이며 MeasureOverride = PageLayout.Extent 이다. 보이는 부분만 그린다.
/// 페이지마다: 회색 바탕 → 썸네일(늘려서) → 이전 배율 타일(늘려서) → 현재 배율 타일.
/// UI 동작(스크롤·입력)은 직접 확인한 것만 보고서에 적는다. 계산은 PageLayout·ZoomMath·PrefetchPlanner가 맡는다.
/// </summary>
public sealed class DocumentView : Control
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

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            Scheduler.Clear();
            Renderer.Dispose();
            foreach (var p in Session.Pages) { Tiles.RemovePage(p.Id); Thumbs.RemovePage(p.Id); }
        }
    }

    private readonly Dictionary<DocumentTabViewModel, ViewState> _states = new();
    private ViewState? _state;
    private ScrollViewer? _sv;
    private PageLayout? _layout;
    private ImmutableArray<PageInfo> _layoutPages = [];
    private double _layoutViewportW = -1;
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
    }

    public DocumentTabViewModel? Tab { get => GetValue(TabProperty); set => SetValue(TabProperty, value); }

    /// <summary>마지막으로 그린 프레임에서 뷰포트 안 페이지 면적 중 그림으로 덮인 비율(0~1). Task 18 벤치에서 읽는다.</summary>
    public double LastCoverage { get; private set; } = 1.0;

    private Rect Viewport => _sv is null ? default : new Rect(_sv.Offset.X, _sv.Offset.Y, _sv.Viewport.Width, _sv.Viewport.Height);

    // ---- 연결 ----

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _sv = this.FindAncestorOfType<ScrollViewer>();
        if (_sv is not null) _sv.ScrollChanged += OnScrollChanged;
        SwitchTab(Tab);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
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
        if (_state is not null)
        {
            if (_sv is not null) _state.Offset = _sv.Offset;
            _state.Tab.PropertyChanged -= OnTabPropertyChanged;
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
        return st;
    }

    private void DisposeState(ViewState st)
    {
        if (ReferenceEquals(_state, st))
        {
            st.Tab.PropertyChanged -= OnTabPropertyChanged;
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
            case nameof(DocumentTabViewModel.Scale): OnScaleChanged(); break;
            case nameof(DocumentTabViewModel.ViewMode): OnViewModeChanged(); break;
            case nameof(DocumentTabViewModel.CurrentPageIndex):
                if (!_fromScroll) GoToPage(_state.Tab.CurrentPageIndex);
                break;
            case nameof(DocumentTabViewModel.PageCount): OnPagesInvalidated(_state); break;
            case nameof(DocumentTabViewModel.Tool): Cursor = CursorFor(_state.Tab.Tool); break;
        }
    }

    private static Cursor CursorFor(ToolKind t) =>
        new(t == ToolKind.Hand ? StandardCursorType.Hand : StandardCursorType.Ibeam);

    private void RebuildLayout()
    {
        if (_state is null) { _layout = null; return; }
        var pages = _state.Session.Pages;
        _layoutPages = pages;
        _layoutViewportW = _sv?.Viewport.Width ?? 0;
        _layout = PageLayout.Build(pages, _state.Tab.Scale, _layoutViewportW, _state.Tab.ViewMode,
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
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));   // 빈 곳도 포인터를 받도록
        var st = _state; var lay = _layout;
        if (st is null || lay is null || _sv is null) { LastCoverage = 1.0; return; }
        var vp = Viewport;
        var pages = _layoutPages;
        var painted = new List<Rect>();
        var pageRects = new List<Rect>();
        double scale = lay.Scale;
        int scaleKey = TileKey.ToScaleKey(scale);

        foreach (int i in lay.PagesIntersecting(vp))
        {
            var info = pages[i];
            var pr = lay.GetPageRect(i);
            pageRects.Add(pr);
            using (ctx.PushClip(pr))
            {
                ctx.FillRectangle(PlaceholderBrush, pr);
                if (st.Thumbs.TryGet(info.Id, info.Version, out var th) && th is BitmapHandle tb)
                {
                    ctx.DrawImage(tb.Bitmap, new Rect(0, 0, tb.Width, tb.Height), pr);
                    painted.Add(pr);
                }
                foreach (var (key, img) in st.Tiles.FallbackFor(info.Id, info.Version, scaleKey))
                    if (img is BitmapHandle fb)
                    {
                        var dest = TileDest(info, key, pr, scale);
                        ctx.DrawImage(fb.Bitmap, new Rect(0, 0, fb.Width, fb.Height), dest);
                        painted.Add(dest.Intersect(pr));
                    }
                foreach (var key in TileGrid.Tiles(info, scale, pr, vp, 0))
                    if (st.Tiles.TryGet(key, out var img) && img is BitmapHandle cb)
                    {
                        var dest = TileDest(info, key, pr, scale);
                        ctx.DrawImage(cb.Bitmap, new Rect(0, 0, cb.Width, cb.Height), dest);
                        painted.Add(dest.Intersect(pr));
                    }
            }
            // ⑤ 덧그림(검색 강조·텍스트 선택)은 Task 15에서 여기에 추가한다.
        }
        LastCoverage = ViewportCoverage.Compute(vp, pageRects, painted);
    }

    /// <summary>타일(키의 배율 기준 픽셀)을 지금 배율의 문서 좌표 사각형으로 바꾼다.</summary>
    private static Rect TileDest(PageInfo info, TileKey key, Rect pageRect, double scale)
    {
        var pb = key.PixelBounds(info);
        double k = scale / key.Scale;
        return new Rect(pageRect.X + pb.X * k, pageRect.Y + pb.Y * k, pb.W * k, pb.H * k);
    }

    private void UpdateWanted()
    {
        var st = _state; var lay = _layout;
        if (st is null || lay is null || _sv is null) return;
        var plan = PrefetchPlanner.Plan(_layoutPages, lay, Viewport, lay.Scale,
            k => st.Tiles.TryGet(k, out _), (id, v) => st.Thumbs.TryGet(id, v, out _));
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
        if (_state.Tab.Tool == ToolKind.Hand && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            _dragStartPos = e.GetPosition(_sv);
            _dragStartOffset = _sv.Offset;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_state is null || _sv is null) return;
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
        if (_dragging) { _dragging = false; e.Pointer.Capture(null); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _state?.Tab.SetCursorFromPagePoint(null, 1);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_state is null || _sv is null || _layout is null || e.KeyModifiers != KeyModifiers.None) return;
        var tab = _state.Tab;
        bool single = _layout.Mode == ViewMode.SinglePage;
        switch (e.Key)
        {
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

    private Vector ClampOffset(double x, double y) =>
        new(Math.Clamp(x, 0, Math.Max(0, _sv!.Extent.Width - _sv.Viewport.Width)),
            Math.Clamp(y, 0, Math.Max(0, _sv.Extent.Height - _sv.Viewport.Height)));
}
