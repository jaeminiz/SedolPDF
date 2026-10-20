using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Content;

namespace PdfEditor.App.Viewer.Objects;

public enum ObjectHandle { None, Body, BottomLeft, BottomRight, TopLeft, TopRight }

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

    public SelectedObject? Selected { get; private set; }

    /// <summary>끄는 중의 새 경계(쪽 좌표). 끌지 않을 때 null.</summary>
    public PdfRectF? Preview { get; private set; }

    public bool IsDragging => _drag != ObjectHandle.None;

    public event Action? Changed;

    public void Select(SelectedObject? o)
    {
        CancelDrag();
        Selected = o;
        Changed?.Invoke();
    }

    public void Clear() => Select(null);

    /// <summary>쪽 객체 목록이 새로 나오면(편집 뒤 버전이 올라감) 같은 객체의 경계를 따라가고, 사라졌으면 선택을 푼다.</summary>
    public void Refresh(PageObjectIndex idx)
    {
        if (Selected is not { } s || s.Page != idx.Page || IsDragging) return;
        var e = idx.Entries.FirstOrDefault(x => x.Ref == s.Ref);
        Select(e is null ? null : s with { Bounds = e.Bounds, Type = e.Type });
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

    /// <summary>선택한 객체의 모서리 손잡이(화면 8 DIP) 또는 몸통.</summary>
    public ObjectHandle HitHandle(PdfPointF p, float ptPerDip)
    {
        if (Selected is not { } s) return ObjectHandle.None;
        float h = (float)(HandleDip / 2) * ptPerDip;
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
        _origin = Selected.Bounds;
        Preview = _origin;
    }

    public void Drag(PdfPointF p, bool shift)
    {
        if (!IsDragging || Selected is not { } s) return;
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
    }

    /// <summary>쪽 좌표에서 사각형 from을 to로 옮기는 행렬(크기 비율 + 이동).</summary>
    public static PdfMatrix MapRect(PdfRectF from, PdfRectF to)
    {
        float sx = from.Width > 0 ? to.Width / from.Width : 1, sy = from.Height > 0 ? to.Height / from.Height : 1;
        return new PdfMatrix(sx, 0, 0, sy, to.Left - sx * from.Left, to.Bottom - sy * from.Bottom);
    }
}
