using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Viewer.Annotations;

/// <summary>
/// Body: 몸통(이동). N~NW: 상자 손잡이 8개. Vertex: 선·폴리라인·다각형의 꼭짓점(HitResult.Index번).
/// LeaderTip: 설명선 지시선 끝점(Vertices[0]).
/// </summary>
public enum HandleKind { None, Body, N, NE, E, SE, S, SW, W, NW, Vertex, LeaderTip }

public sealed record HitResult(AnnotId Id, HandleKind Handle, int Index = 0);

/// <summary>
/// 선택·이동·크기 조절·삭제 로직. Avalonia를 참조하지 않는다(좌표는 PDF pt, 화면 고정 크기는 ptPerDip로 환산).
/// 끌기 한 번 = 명령 하나. 시작 때 스냅숏의 현재 주석을 잡고 End에서 그 주석을 before로 쓴다.
/// 텍스트 바꾸기 묶음(StrikeOut + Caret)은 하나를 고르면 둘 다 선택되고, 이동·삭제도 한 명령이다.
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
    private int _vertex;
    private PdfPointF _start;
    private PdfRectF _bounds;
    private float _ptPerDip = 1f;
    private readonly List<Annotation> _targets = [];
    private PdfRectF _union;
    private PdfRectF _startRect;
    private float _dx, _dy;
    private PdfRectF _newRect;
    private float _grabX, _grabY;   // 손잡이 중심에서 잡은 점까지의 거리 (끌 때 가장자리가 튀지 않게)

    /// <summary>끌기 중 바뀐 주석의 임시 모습. 끌기 중이 아니면 null.</summary>
    public IReadOnlyDictionary<AnnotId, Annotation?>? Previews { get; private set; }

    /// <summary>선택한 주석 하나에 그릴 손잡이들 (화면 표시와 HitTest가 같은 목록을 쓴다).</summary>
    public static IReadOnlyList<(HandleKind Kind, int Index, PdfPointF Center)> HandlesOf(Annotation a)
    {
        var list = new List<(HandleKind, int, PdfPointF)>();
        switch (a.Kind)
        {
            case AnnotKind.Line or AnnotKind.PolyLine or AnnotKind.Polygon when !a.Vertices.IsDefaultOrEmpty:
                for (int i = 0; i < a.Vertices.Length; i++) list.Add((HandleKind.Vertex, i, a.Vertices[i]));
                break;
            case AnnotKind.FreeText:
                if (a.Intent == FreeTextIntent.Callout && !a.Vertices.IsDefaultOrEmpty)
                    list.Add((HandleKind.LeaderTip, 0, a.Vertices[0]));
                foreach (var (k, c) in HandleCenters(BoxOf(a))) list.Add((k, 0, c));
                break;
            default:
                if (AnnotationGeometry.CanResize(a.Kind) && a.Kind is not (AnnotKind.Line or AnnotKind.PolyLine or AnnotKind.Polygon))
                    foreach (var (k, c) in HandleCenters(AnnotationGeometry.ComputeRect(a))) list.Add((k, 0, c));
                break;
        }
        return list;
    }

    /// <summary>글 상자: TextBox가 있으면 그것, 없으면 Rect.</summary>
    private static PdfRectF BoxOf(Annotation a) => a.TextBox ?? a.Rect;

    public HitResult? HitTest(PageAnnotations page, AnnotationSelection sel, PdfPointF p, float ptPerDip)
    {
        if (sel.Page == page.Page && sel.Ids.Count == 1)
        {
            var a = page.Items.Select(v => v.Data).FirstOrDefault(x => x.Id == sel.Ids[0]);
            if (a is not null)
            {
                float half = HandleDip / 2f * ptPerDip;
                foreach (var (kind, index, c) in HandlesOf(a))
                    if (Math.Abs(p.X - c.X) <= half && Math.Abs(p.Y - c.Y) <= half)
                        return new HitResult(a.Id, kind, index);
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

    /// <summary>묶음(텍스트 바꾸기)의 다른 구성원까지 넣은 Id 목록. 묶이지 않은 주석은 그대로.</summary>
    public static IReadOnlyList<AnnotId> ExpandGroups(PageAnnotations page, IEnumerable<AnnotId> ids)
    {
        var result = new List<AnnotId>();
        foreach (var id in ids)
        {
            var group = page.GroupOf(id);   // 대표(StrikeOut)가 맨 앞
            if (group.Count == 0) { if (!result.Contains(id)) result.Add(id); continue; }
            foreach (var m in group)
                if (!result.Contains(m.Id)) result.Add(m.Id);
        }
        return result;
    }

    /// <summary>끌기 시작. Body를 선택에 없는 주석 위에서 시작하면 그 주석(묶음이면 묶음 전체)으로 선택을 바꾼다.</summary>
    public void Begin(PageAnnotations page, AnnotationSelection sel, HitResult hit, PdfPointF p, PdfRectF pageBounds,
        float ptPerDip)
    {
        Cancel();
        _page = page.Page;
        _start = p;
        _bounds = pageBounds;
        _ptPerDip = ptPerDip;
        _handle = hit.Handle;
        _vertex = hit.Index;

        if (hit.Handle == HandleKind.Body)
        {
            IReadOnlyList<AnnotId> current = sel.Page == page.Page && sel.Ids.Contains(hit.Id) ? sel.Ids : [hit.Id];
            var expanded = ExpandGroups(page, current);
            if (!(sel.Page == page.Page && sel.Ids.SequenceEqual(expanded))) sel.Set(page.Page, expanded);
        }

        IReadOnlyList<AnnotId> ids = hit.Handle == HandleKind.Body ? sel.Ids : [hit.Id];
        // 스냅숏의 현재 인스턴스만 쓴다 (낡은 before는 Apply가 거부한다)
        foreach (var v in page.Items)
            if (ids.Contains(v.Data.Id)) _targets.Add(v.Data);
        if (_targets.Count == 0 || hit.Handle == HandleKind.None) { _targets.Clear(); return; }
        if (_handle != HandleKind.Body && (_targets.Count != 1 || !HasHandle(_targets[0], _handle, _vertex)))
        {
            _targets.Clear();
            return;
        }

        if (_handle == HandleKind.Body)
        {
            _union = AnnotationGeometry.ComputeRect(_targets[0]);
            for (int i = 1; i < _targets.Count; i++) _union = _union.Union(AnnotationGeometry.ComputeRect(_targets[i]));
            _startRect = _union;
        }
        else
        {
            var t = _targets[0];
            _union = AnnotationGeometry.ComputeRect(t);
            _startRect = t.Kind == AnnotKind.FreeText ? BoxOf(t) : _union;
        }
        _newRect = _startRect;
        _grabX = _grabY = 0;
        if (_handle is HandleKind.Vertex or HandleKind.LeaderTip)
        {
            var v = _targets[0].Vertices[_handle == HandleKind.LeaderTip ? 0 : _vertex];
            _grabX = p.X - v.X; _grabY = p.Y - v.Y;
        }
        else if (_handle != HandleKind.Body)
            foreach (var (k, c) in HandleCenters(_startRect))
                if (k == _handle) { _grabX = p.X - c.X; _grabY = p.Y - c.Y; }
        _active = true;
    }

    // 현재 스냅숏의 주석에 그 손잡이가 실제로 있는지 (낡은 HitResult 방어)
    private static bool HasHandle(Annotation a, HandleKind kind, int index)
    {
        foreach (var (k, i, _) in HandlesOf(a))
            if (k == kind && i == index) return true;
        return false;
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
        else if (_handle is HandleKind.Vertex or HandleKind.LeaderTip)
        {
            // 끈 꼭짓점 하나만 옮긴다 (나머지 점은 그대로)
            var t = _targets[0];
            int idx = _handle == HandleKind.LeaderTip ? 0 : _vertex;
            var old = t.Vertices[idx];
            var np = new PdfPointF(
                Math.Clamp(p.X - _grabX, _bounds.Left, _bounds.Right), Math.Clamp(p.Y - _grabY, _bounds.Bottom, _bounds.Top));
            _dx = np.X - old.X; _dy = np.Y - old.Y;
            var moved = t with { Vertices = t.Vertices.SetItem(idx, np) };
            moved = ReAnchorLeader(moved);
            map[t.Id] = moved with { Rect = AnnotationGeometry.ComputeRect(moved) };
        }
        else
        {
            _newRect = ComputeResizeRect(p);
            var t = _targets[0];
            map[t.Id] = t.Kind == AnnotKind.FreeText ? WithBox(t, _newRect) : AnnotationGeometry.Resize(t, _newRect);
        }
        Previews = map;
    }

    // FreeText는 글 상자만 바꾼다 (글자 크기·지시선 그대로). 새 상자는 끄는 손잡이로 이미 계산된 값을 그대로 쓴다
    // (AnnotationGeometry.Resize는 최소 크기를 Left/Bottom 기준으로 맞춰 N·W 손잡이에서 튈 수 있다).
    private static Annotation WithBox(Annotation a, PdfRectF box)
    {
        var b = a.TextBox is null ? a with { Rect = box } : a with { TextBox = box };
        b = ReAnchorLeader(b);
        return b with { Rect = AnnotationGeometry.ComputeRect(b) };
    }

    // 설명선: 지시선의 상자 쪽 끝(마지막 점)을 끝점에서 가장 가까운 변의 가운데로 다시 붙인다. 점 개수(2~3)는 그대로.
    private static Annotation ReAnchorLeader(Annotation a)
    {
        if (a.Intent != FreeTextIntent.Callout || a.TextBox is not { } box || a.Vertices.IsDefaultOrEmpty || a.Vertices.Length < 2) return a;
        var verts = a.Vertices.SetItem(a.Vertices.Length - 1, AnnotationToolController.LeaderAttachPoint(box, a.Vertices[0]));
        var b = a with { Vertices = verts };
        return b with { Rect = AnnotationGeometry.ComputeRect(b) };
    }

    public ChangeAnnotationsCommand? End(PdfPointF p)
    {
        if (!_active) return null;
        Move(p);
        var now = clock();
        float minPt = MinMoveDip * _ptPerDip;
        bool moved = _handle is HandleKind.Body or HandleKind.Vertex or HandleKind.LeaderTip
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

    /// <summary>선택한 주석(묶음이면 묶음 전체)을 지우는 명령. 호출한 쪽이 실행한 뒤 선택을 Clear/Prune해야 한다.</summary>
    public ChangeAnnotationsCommand? DeleteSelected(PageAnnotations page, AnnotationSelection sel)
    {
        if (sel.Page != page.Page || sel.Ids.Count == 0) return null;
        var ids = ExpandGroups(page, sel.Ids);
        var items = page.Items.Select(v => v.Data).Where(a => ids.Contains(a.Id)).ToList();
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
        float px = Math.Clamp(p.X - _grabX, _bounds.Left, _bounds.Right), py = Math.Clamp(p.Y - _grabY, _bounds.Bottom, _bounds.Top);
        if (_handle is HandleKind.E or HandleKind.NE or HandleKind.SE) rt = Math.Max(px, l + min);
        if (_handle is HandleKind.W or HandleKind.NW or HandleKind.SW) l = Math.Min(px, rt - min);
        if (_handle is HandleKind.N or HandleKind.NE or HandleKind.NW) t = Math.Max(py, b + min);
        if (_handle is HandleKind.S or HandleKind.SE or HandleKind.SW) b = Math.Min(py, t - min);
        return new PdfRectF(l, b, rt, t);
    }
}
