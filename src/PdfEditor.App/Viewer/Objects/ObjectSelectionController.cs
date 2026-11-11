using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Content;

namespace PdfEditor.App.Viewer.Objects;

public enum ObjectHandle { None, Body, BottomLeft, BottomRight, TopLeft, TopRight, Rotate }

/// <summary>고른 쪽 객체 하나(M4는 한 번에 하나, 결정 C8). Bounds는 쪽 좌표(pt).</summary>
public sealed record SelectedObject(PageId Page, ObjectRef Ref, PdfPageObjectType Type, PdfRectF Bounds);

/// <summary>
/// 쪽 객체 선택·옮기기·크기 바꾸기의 계산(Avalonia 없음, UI 스레드에서 씀). 결정 C8·C9:
/// 맨 위 객체(쪽 테두리는 다른 후보가 없을 때만), Alt+클릭은 한 단계 아래, 끄는 동안은 점선 미리보기만,
/// 이미지·텍스트·폼은 비율 고정(Shift로 자유), 도형은 자유(Shift로 고정). 놓으면 옛 경계 → 새 경계 행렬 하나.
/// </summary>
public sealed class ObjectSelectionController
{
    public const double HandleDip = 8;
    public const float MinSizePt = 1f;

    private ObjectHandle _drag;
    private PdfPointF _start;
    private PdfRectF _origin;

    private readonly List<SelectedObject> _all = [];

    /// <summary>붙여넣기·복제로 만든 개체의 넣기 번호. 개체 편집 도구의 사각형 끌기가 (편집 탭으로 넣은 개체와 달리) 이것들은 함께 고른다.</summary>
    public HashSet<long> PastedIds { get; } = [];

    /// <summary>대표(첫 번째) 선택. 여러 개를 골랐으면 크기 조절은 못 하고 함께 옮기기·지우기만 한다.</summary>
    public SelectedObject? Selected => _all.Count > 0 ? _all[0] : null;

    /// <summary>고른 객체 모두(같은 쪽).</summary>
    public IReadOnlyList<SelectedObject> All => _all;

    /// <summary>끄는 동안 대표 객체가 옮겨진 양(쪽 좌표). 여러 개를 고른 경우 모두 같은 양만큼 옮겨 그린다.</summary>
    public (float Dx, float Dy) DragDelta => Preview is { } p && IsDragging ? (p.Left - _origin.Left, p.Bottom - _origin.Bottom) : (0, 0);

    /// <summary>끄는 중의 새 경계(쪽 좌표). 끌지 않을 때 null.</summary>
    public PdfRectF? Preview { get; private set; }

    public bool IsDragging => _drag != ObjectHandle.None;

    public event Action? Changed;

    public void Select(SelectedObject? o)
    {
        CancelDrag();
        _all.Clear();
        if (o is not null) _all.Add(o);
        Changed?.Invoke();
    }

    /// <summary>한꺼번에 여러 개 고르기(사각형 끌기). 모두 같은 쪽이어야 한다.</summary>
    public void SelectMany(IEnumerable<SelectedObject> items)
    {
        CancelDrag();
        _all.Clear();
        var list = items.ToList();
        if (list.Count > 0) _all.AddRange(list.Where(x => x.Page == list[0].Page));
        Changed?.Invoke();
    }

    /// <summary>Ctrl/Shift+클릭: 이미 골랐으면 빼고, 아니면 더한다(다른 쪽 선택은 버린다).</summary>
    public void Toggle(SelectedObject o)
    {
        CancelDrag();
        if (_all.Count > 0 && _all[0].Page != o.Page) _all.Clear();
        int at = _all.FindIndex(x => x.Ref == o.Ref);
        if (at >= 0) _all.RemoveAt(at); else _all.Add(o);
        Changed?.Invoke();
    }

    public void Clear() => Select(null);

    /// <summary>쪽 객체 목록이 새로 나오면(편집 뒤 버전이 올라감) 같은 객체의 경계를 따라가고, 사라졌으면 선택을 푼다.</summary>
    public void Refresh(PageObjectIndex idx)
    {
        if (_all.Count == 0 || _all[0].Page != idx.Page || IsDragging) return;
        // 사각형 끌기로 수천 개를 골랐을 수 있다 — 선택 수 x 객체 수(최대 20만)로 훑지 않고 표를 한 번 만든다(UI 스레드).
        var byRef = new Dictionary<ObjectRef, PageObjectEntry>();
        foreach (var x in idx.Entries) byRef.TryAdd(x.Ref, x);
        var next = new List<SelectedObject>();
        foreach (var s in _all)
            if (byRef.TryGetValue(s.Ref, out var e)) next.Add(s with { Bounds = e.Bounds, Type = e.Type });
        SelectMany(next);
    }

    /// <summary>점 p를 덮는 객체 중 고를 것. cycle(Alt)이면 지금 선택 바로 아래 것(맨 아래면 다시 맨 위).</summary>
    public PageObjectEntry? Pick(PageObjectIndex idx, PdfPointF p, float tolerancePt, bool cycle, Func<PageObjectEntry, bool>? filter = null)
    {
        var hits = idx.HitTest(p, tolerancePt, filter);
        if (hits.Count == 0) return null;
        if (cycle && Selected is { } s && s.Page == idx.Page)
        {
            int at = hits.ToList().FindIndex(h => h.Ref == s.Ref);
            if (at >= 0) return hits[(at + 1) % hits.Count];
        }
        return hits[0];
    }

    public const double RotateHandleOffsetDip = 24;

    /// <summary>고른 객체 모두를 감싼 경계(쪽 좌표).</summary>
    public PdfRectF GroupBounds => _all.Count == 0 ? default : PasteOffset.UnionRects(_all.Select(x => x.Bounds));

    /// <summary>
    /// 회전 손잡이 자리(쪽 좌표): 화면에서 경계 위쪽 가운데에서 RotateHandleOffsetDip 위. 회전된 쪽은 화면 위쪽이 쪽 좌표의 다른 방향이다.
    /// </summary>
    public static PdfPointF RotateHandlePos(PdfRectF b, int pageRotation, float ptPerDip)
    {
        float d = (float)RotateHandleOffsetDip * ptPerDip;
        float cx = (b.Left + b.Right) / 2, cy = (b.Bottom + b.Top) / 2;
        return (((pageRotation % 4) + 4) % 4) switch
        {
            0 => new PdfPointF(cx, b.Top + d),
            1 => new PdfPointF(b.Left - d, cy),
            2 => new PdfPointF(cx, b.Bottom - d),
            _ => new PdfPointF(b.Right + d, cy),
        };
    }

    /// <summary>끄는 중인 회전 각(라디안, 쪽 좌표 기준 반시계가 +). 회전하지 않을 때 0.</summary>
    public float RotationPreview { get; private set; }

    private float _angle0;

    /// <summary>회전 손잡이를 끄는 중인가.</summary>
    public bool IsRotating => IsDragging && _drag == ObjectHandle.Rotate;

    /// <summary>회전을 시작한 방향(쪽 좌표, 라디안): 중심에서 처음 누른 곳을 본 각. 부채꼴 그리기에 쓴다.</summary>
    public float RotationStartAngle => _angle0;

    /// <summary>선택한 객체의 회전 손잡이, 모서리 손잡이(화면 8 DIP) 또는 몸통.</summary>
    public ObjectHandle HitHandle(PdfPointF p, float ptPerDip, int pageRotation = 0)
    {
        if (Selected is not { } s) return ObjectHandle.None;
        float h = (float)(HandleDip / 2) * ptPerDip;
        var rp = RotateHandlePos(GroupBounds, pageRotation, ptPerDip);
        if (Math.Abs(p.X - rp.X) <= h * 1.3f && Math.Abs(p.Y - rp.Y) <= h * 1.3f) return ObjectHandle.Rotate;
        if (_all.Count > 1) return _all.Any(x => x.Bounds.Inflate(h).Contains(p)) ? ObjectHandle.Body : ObjectHandle.None;
        var b = s.Bounds;
        bool Near(float x, float y) => Math.Abs(p.X - x) <= h && Math.Abs(p.Y - y) <= h;
        if (Near(b.Left, b.Bottom)) return ObjectHandle.BottomLeft;
        if (Near(b.Right, b.Bottom)) return ObjectHandle.BottomRight;
        if (Near(b.Left, b.Top)) return ObjectHandle.TopLeft;
        if (Near(b.Right, b.Top)) return ObjectHandle.TopRight;
        return b.Inflate(h).Contains(p) ? ObjectHandle.Body : ObjectHandle.None;
    }

    public void BeginDrag(ObjectHandle handle, PdfPointF start)
    {
        if (Selected is null || handle == ObjectHandle.None) return;
        _drag = handle;
        _start = start;
        if (handle == ObjectHandle.Rotate)
        {
            _origin = GroupBounds;
            Preview = _origin;
            RotationPreview = 0;
            _angle0 = AngleAround(start);
            return;
        }
        _origin = Selected.Bounds;
        Preview = _origin;
    }

    public void Drag(PdfPointF p, bool shift)
    {
        if (!IsDragging || Selected is not { } s) return;
        if (_drag == ObjectHandle.Rotate)
        {
            float a = AngleAround(p) - _angle0;
            if (shift) a = (float)(Math.Round(a / (Math.PI / 12)) * (Math.PI / 12));   // Shift: 15도 단위
            RotationPreview = a;
            Changed?.Invoke();
            return;
        }
        float dx = p.X - _start.X, dy = p.Y - _start.Y;
        var o = _origin;
        if (_drag == ObjectHandle.Body) { Preview = o.Offset(dx, dy); Changed?.Invoke(); return; }

        // 반대쪽 모서리를 고정
        float ax = _drag is ObjectHandle.BottomLeft or ObjectHandle.TopLeft ? o.Right : o.Left;
        float ay = _drag is ObjectHandle.BottomLeft or ObjectHandle.BottomRight ? o.Top : o.Bottom;
        float w = Math.Max(MinSizePt, Math.Abs(p.X - ax)), hgt = Math.Max(MinSizePt, Math.Abs(p.Y - ay));
        bool keepAspect = (s.Type is PdfPageObjectType.Image or PdfPageObjectType.Text or PdfPageObjectType.Form) != shift;
        if (keepAspect && o.Width > 0 && o.Height > 0)
        {
            float k = Math.Max(w / o.Width, hgt / o.Height);
            w = Math.Max(MinSizePt, o.Width * k);
            hgt = Math.Max(MinSizePt, o.Height * k);
        }
        float left = _drag is ObjectHandle.BottomLeft or ObjectHandle.TopLeft ? ax - w : ax;
        float bottom = _drag is ObjectHandle.BottomLeft or ObjectHandle.BottomRight ? ay - hgt : ay;
        Preview = new PdfRectF(left, bottom, left + w, bottom + hgt);
        Changed?.Invoke();
    }

    /// <summary>끌기를 끝낸다. 바뀌었으면 옛 경계 → 새 경계 행렬(TransformEdit에 그대로), 아니면 null.</summary>
    public PdfMatrix? EndDrag()
    {
        if (!IsDragging) return null;
        if (_drag == ObjectHandle.Rotate)
        {
            float ang = RotationPreview; var c = _origin;
            CancelDrag();
            Changed?.Invoke();
            if (Math.Abs(ang) < 0.002f) return null;
            return RotateAbout(ang, (c.Left + c.Right) / 2, (c.Bottom + c.Top) / 2);
        }
        var from = _origin;
        var to = Preview ?? from;
        CancelDrag();
        Changed?.Invoke();
        const float eps = 0.01f;
        if (Math.Abs(to.Left - from.Left) < eps && Math.Abs(to.Bottom - from.Bottom) < eps
            && Math.Abs(to.Width - from.Width) < eps && Math.Abs(to.Height - from.Height) < eps) return null;
        return MapRect(from, to);
    }

    public void CancelDrag()
    {
        _drag = ObjectHandle.None;
        Preview = null;
        RotationPreview = 0;
    }

    private float AngleAround(PdfPointF p)
    {
        var c = _origin;
        return (float)Math.Atan2(p.Y - (c.Bottom + c.Top) / 2, p.X - (c.Left + c.Right) / 2);
    }

    /// <summary>(cx, cy)를 중심으로 angle 라디안(반시계) 돌리는 행렬.</summary>
    public static PdfMatrix RotateAbout(float angle, float cx, float cy)
    {
        float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
        return PdfMatrix.Translate(-cx, -cy).Then(new PdfMatrix(cos, sin, -sin, cos, 0, 0)).Then(PdfMatrix.Translate(cx, cy));
    }

    /// <summary>쪽 좌표에서 사각형 from을 to로 옮기는 행렬(크기 비율 + 이동).</summary>
    public static PdfMatrix MapRect(PdfRectF from, PdfRectF to)
    {
        float sx = from.Width > 0 ? to.Width / from.Width : 1, sy = from.Height > 0 ? to.Height / from.Height : 1;
        return new PdfMatrix(sx, 0, 0, sy, to.Left - sx * from.Left, to.Bottom - sy * from.Bottom);
    }
}
