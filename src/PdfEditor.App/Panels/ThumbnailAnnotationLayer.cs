using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Pdf.Native;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Panels;

/// <summary>
/// 썸네일 비트맵 위의 주석 층. DataContext = ThumbnailItem(세션·PageId). 문서 화면과 같은 AnnotationOverlay를 썸네일 배율로 쓴다.
/// 쪽 경계·회전은 전용 스레드에서 한 번 읽어 캐시한다(쪽 버전이 바뀌면 다시).
/// </summary>
public sealed class ThumbnailAnnotationLayer : Control
{
    private static readonly IReadOnlyDictionary<AnnotId, Annotation?> NoPreviews = new Dictionary<AnnotId, Annotation?>();

    private bool _attached;
    private ThumbnailItem? _item;
    private DocumentSession? _session;
    private (System.Drawing.RectangleF Bounds, int Rotation, int Version)? _geom;
    private int _geomRequestedVersion = -1;
    private Action<AnnotationsChangedEventArgs>? _handler;

    public ThumbnailAnnotationLayer() => IsHitTestVisible = false;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Unhook();
        _item = DataContext as ThumbnailItem;
        Hook();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Hook();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Unhook();
        base.OnDetachedFromVisualTree(e);
    }

    private void Hook()
    {
        if (_item is null || _session is not null || !_attached) return;
        _session = _item.Session;
        if (_session is null) return;
        _item.PropertyChanged += OnItemChanged;
        var item = _item;
        _handler = e =>
        {
            if (!e.Pages.Contains(item.Id)) return;
            Dispatcher.UIThread.Post(InvalidateVisual);   // 전용 스레드에서 오므로 UI로 넘긴다
        };
        _session.Annotations.Changed += _handler;
    }

    private void Unhook()
    {
        if (_item is not null) _item.PropertyChanged -= OnItemChanged;
        if (_session is not null && _handler is not null) _session.Annotations.Changed -= _handler;
        _session = null;
        _handler = null;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ThumbnailItem.Version) or nameof(ThumbnailItem.DisplayWidth) or nameof(ThumbnailItem.DisplayHeight))
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var item = _item; var session = _session;
        if (item is null || session is null) return;
        var annots = session.Annotations.Get(item.Id);
        if (!annots.Items.Any(i => i.ModelDrawn)) return;
        int version = item.Version;
        if (_geom is not { } g || g.Version != version)
        {
            RequestGeom(session, item.Id, version);
            return;   // 도착하면 다시 그린다
        }
        var probe = new PageTransform(g.Bounds, g.Rotation, 1.0).ViewSize;
        if (probe.W <= 0 || probe.H <= 0) return;
        double scale = Bounds.Width / probe.W;
        var tr = new PageTransform(g.Bounds, g.Rotation, scale);
        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        using (context.PushClip(rect))
            AnnotationOverlay.DrawPage(context, annots, rect, tr, g.Rotation, NoPreviews);
    }

    private void RequestGeom(DocumentSession session, PageId id, int version)
    {
        if (_geomRequestedVersion == version) return;
        _geomRequestedVersion = version;
        _ = Load();

        async Task Load()
        {
            (System.Drawing.RectangleF, int)? r = null;
            try
            {
                r = await session.Thread.InvokeAsync(() =>
                {
                    if (session.IndexOf(id) < 0) return ((System.Drawing.RectangleF, int)?)null;
                    var p = session.GetLoadedPage(id);
                    return (p.BoundsPt, p.Rotation);
                });
            }
            catch (Exception) { }   // 스레드가 닫혔거나 쪽이 없어짐: 주석 층만 생략한다.
            Dispatcher.UIThread.Post(() =>
            {
                if (_geomRequestedVersion != version) return;
                if (r is { } v) _geom = (v.Item1, v.Item2, version);
                else _geomRequestedVersion = -1;
                InvalidateVisual();
            });
        }
    }
}
