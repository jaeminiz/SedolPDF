using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Viewer.Annotations;

public enum HandleKind { None, Body, N, NE, E, SE, S, SW, W, NW }

public sealed record HitResult(AnnotId Id, HandleKind Handle);

/// <summary>
/// 선택·이동·크기 조절·삭제 로직. Avalonia를 참조하지 않는다(좌표는 PDF pt, 화면 고정 크기는 ptPerDip로 환산).
/// 끌기 한 번 = 명령 하나. 시작 때 스냅숏의 현재 주석을 잡고 End에서 그 주석을 before로 쓴다.
/// 알려진 한계: 손대지 않은 원본(PDFium이 그림)을 끌면 미리보기가 타일의 원래 모양과 겹쳐 보인다.
/// </summary>
public sealed class SelectionController(Func<DateTimeOffset> clock)
{
    public const float HandleDip = 8f;
    public const float HitToleranceDip = 4f;
    public const float MinMoveDip = 1f;

    private bool _active;
    private PageId _page;
    private HandleKind _handle;
    private PdfPointF _start;
    private PdfRectF _bounds;
    private float _ptPerDip = 1f;
    private readonly List<Annotation> _targets = [];
    private PdfRectF _union;
    private PdfRectF _startRect;
    private float _dx, _dy;
    private PdfRectF _newRect;

    /// <summary>끌기 중 바뀐 주석의 임시 모습. 끌기 중이 아니면 null.</summary>
    public IReadOnlyDictionary<AnnotId, Annotation?>? Previews { get; private set; }

    public HitResult? HitTest(PageAnnotations page, AnnotationSelection sel, PdfPointF p, float ptPerDip)
    {
        if (sel.Page == page.Page && sel.Ids.Count == 1)
        {
            var a = page.Items.Select(v => v.Data).FirstOrDefault(x => x.Id == sel.Ids[0]);
            if (a is not null && AnnotationGeometry.CanResize(a.Kind))
            {
                var r = AnnotationGeometry.ComputeRect(a);
                float half = HandleDip / 2f * ptPerDip;
                foreach (var (kind, c) in HandleCenters(r))
                    if (Math.Abs(p.X - c.X) <= half && Math.Abs(p.Y - c.Y) <= half)
                        return new HitResult(a.Id, kind);
            }
        }
        var id = AnnotationHitTest.TopmostAt([.. page.Items.Select(v => v.Data)], p, HitToleranceDip * ptPerDip);
        return id is { } i ? new HitResult(i, HandleKind.Body) : null;
    }

    private static IEnumerable<(HandleKind, PdfPointF)> HandleCenters(PdfRectF r)
    {
        float mx = (r.Left + r.Right) / 2, my = (r.Bottom + r.Top) / 2;
        yield return (HandleKind.NW, new(r.Left, r.Top));
        yield return (HandleKind.NE, new(r.Right, r.Top));
        yield return (HandleKind.SE, new(r.Right, r.Bottom));
        yield return (HandleKind.SW, new(r.Left, r.Bottom));
        yield return (HandleKind.N, new(mx, r.Top));
        yield return (HandleKind.S, new(mx, r.Bottom));
        yield return (HandleKind.E, new(r.Right, my));
        yield return (HandleKind.W, new(r.Left, my));
    }

    /// <summary>끌기 시작. Body를 선택에 없는 주석 위에서 시작하면 그 주석 하나로 선택을 바꾼다.</summary>
    public void Begin(PageAnnotations page, AnnotationSelection sel, HitResult hit, PdfPointF p, PdfRectF pageBounds,
        float ptPerDip = 1f)
    {
        Cancel();
        _page = page.Page;
        _start = p;
        _bounds = pageBounds;
        _ptPerDip = ptPerDip;
        _handle = hit.Handle;

        if (hit.Handle == HandleKind.Body && !(sel.Page == page.Page && sel.Ids.Contains(hit.Id)))
            sel.Set(page.Page, [hit.Id]);

        IReadOnlyList<AnnotId> ids = hit.Handle == HandleKind.Body ? sel.Ids : [hit.Id];
        // 스냅숏의 현재 인스턴스만 쓴다 (낡은 before는 Apply가 거부한다)
        foreach (var v in page.Items)
            if (ids.Contains(v.Data.Id)) _targets.Add(v.Data);
        if (_targets.Count == 0 || hit.Handle == HandleKind.None) { _targets.Clear(); return; }
        if (_handle != HandleKind.Body && (_targets.Count != 1 || !AnnotationGeometry.CanResize(_targets[0].Kind)))
        {
            _targets.Clear();
            return;
        }

        _union = AnnotationGeometry.ComputeRect(_targets[0]);
        for (int i = 1; i < _targets.Count; i++) _union = _union.Union(AnnotationGeometry.ComputeRect(_targets[i]));
        _startRect = _union;
        _newRect = _startRect;
        _active = true;
    }

    public void Move(PdfPointF p)
    {
        if (!_active) return;
        var map = new Dictionary<AnnotId, Annotation?>();
        if (_handle == HandleKind.Body)
        {
            ComputeDelta(p);
            foreach (var t in _targets) map[t.Id] = AnnotationGeometry.Translate(t, _dx, _dy);
        }
        else
        {
            _newRect = ComputeResizeRect(p);
            map[_targets[0].Id] = AnnotationGeometry.Resize(_targets[0], _newRect);
        }
        Previews = map;
    }

    public ChangeAnnotationsCommand? End(PdfPointF p)
    {
        if (!_active) return null;
        Move(p);
        var now = clock();
        float minPt = MinMoveDip * _ptPerDip;
        bool moved = _handle == HandleKind.Body
            ? Math.Abs(_dx) >= minPt || Math.Abs(_dy) >= minPt
            : Math.Abs(_newRect.Left - _startRect.Left) >= minPt || Math.Abs(_newRect.Right - _startRect.Right) >= minPt
              || Math.Abs(_newRect.Bottom - _startRect.Bottom) >= minPt || Math.Abs(_newRect.Top - _startRect.Top) >= minPt;
        List<(Annotation Before, Annotation After)>? items = null;
        if (moved)
        {
            items = [];
            foreach (var t in _targets)
                items.Add((t, Previews![t.Id]! with { ModifiedAt = now }));
        }
        var page = _page;
        Cancel();
        return items is null ? null : ChangeAnnotationsCommand.Replace(page, items);
    }

    public void Cancel()
    {
        _active = false;
        Previews = null;
        _targets.Clear();
        _dx = _dy = 0;
    }

    public ChangeAnnotationsCommand? DeleteSelected(PageAnnotations page, AnnotationSelection sel)
    {
        if (sel.Page != page.Page || sel.Ids.Count == 0) return null;
        var items = page.Items.Select(v => v.Data).Where(a => sel.Ids.Contains(a.Id)).ToList();
        return items.Count == 0 ? null : ChangeAnnotationsCommand.Delete(page.Page, items);
    }

    private void ComputeDelta(PdfPointF p)
    {
        float dx = p.X - _start.X, dy = p.Y - _start.Y;
        // 합집합이 쪽 경계 안에 머물게 깎는다 (합집합이 경계보다 크면 그 축은 깎지 않는다)
        if (_union.Width <= _bounds.Width)
        {
            dx = Math.Max(dx, _bounds.Left - _union.Left);
            dx = Math.Min(dx, _bounds.Right - _union.Right);
        }
        if (_union.Height <= _bounds.Height)
        {
            dy = Math.Max(dy, _bounds.Bottom - _union.Bottom);
            dy = Math.Min(dy, _bounds.Top - _union.Top);
        }
        _dx = dx; _dy = dy;
    }

    /// <summary>끄는 변만 움직이고 반대쪽 변·모서리는 고정한다 (최소 크기는 끄는 쪽에서 지킨다).</summary>
    private PdfRectF ComputeResizeRect(PdfPointF p)
    {
        var r = _startRect;
        float l = r.Left, b = r.Bottom, rt = r.Right, t = r.Top;
        float min = AnnotationGeometry.MinShapeSizePt;
        float px = Math.Clamp(p.X, _bounds.Left, _bounds.Right), py = Math.Clamp(p.Y, _bounds.Bottom, _bounds.Top);
        if (_handle is HandleKind.E or HandleKind.NE or HandleKind.SE) rt = Math.Max(px, l + min);
        if (_handle is HandleKind.W or HandleKind.NW or HandleKind.SW) l = Math.Min(px, rt - min);
        if (_handle is HandleKind.N or HandleKind.NE or HandleKind.NW) t = Math.Max(py, b + min);
        if (_handle is HandleKind.S or HandleKind.SE or HandleKind.SW) b = Math.Min(py, t - min);
        return new PdfRectF(l, b, rt, t);
    }
}
