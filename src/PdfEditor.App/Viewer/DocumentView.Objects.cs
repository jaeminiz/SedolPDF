using RectangleF = System.Drawing.RectangleF;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 문서 화면의 쪽 객체 편집 입력(M4): 객체 편집·텍스트 편집 도구의 선택, 옮기기, 크기 바꾸기, 지우기, 방향키.
/// 계산은 ObjectSelectionController(Avalonia 없음), 실제 변경은 ContentEditCommand가 한다. 끄는 동안은 점선 미리보기만(C9).
/// 실제 마우스 입력은 직접 확인한 것만 보고서에 적는다(검증 필요).
/// </summary>
public sealed partial class DocumentView
{
    private static bool IsObjectTool(ToolKind t) => t is ToolKind.EditObject or ToolKind.EditText || IsAddTool(t);

    private bool _objDragging;
    private int _objPage = -1;
    private PageGeom? _objGeom;
    private PressInfo? _pendingObjPress;

    // ---- 객체 목록 (전용 스레드에서 읽어 (쪽, 버전)마다 캐시) ----

    private bool TryGetObjectIndex(ViewState st, PageInfo info, out PageObjectIndex idx)
    {
        var key = (info.Id, info.Version);
        if (st.ObjIndex.TryGetValue(key, out idx!)) return true;
        if (st.ObjIndexPending.Add(key)) _ = LoadObjectIndexAsync(st, key);
        return false;
    }

    private async Task LoadObjectIndexAsync(ViewState st, (PageId Id, int Version) key)
    {
        PageObjectIndex? idx = null;
        try
        {
            idx = await st.Session.Thread.InvokeAsync(() =>
                st.Session.IndexOf(key.Id) < 0 ? null : st.Session.GetObjectIndex(key.Id));
        }
        catch (Exception) { }   // 쪽이 없어졌거나 PDFium 스레드가 닫힘: 객체 편집만 못 한다
        Dispatcher.UIThread.Post(() =>
        {
            st.ObjIndexPending.Remove(key);
            if (st.Disposed || idx is null) return;
            // 객체 목록의 버전이 다를 수 있다(그사이 또 편집): 그 버전으로 저장해 둔다
            st.ObjIndex[(idx.Page, idx.Version)] = idx;
            foreach (var old in st.ObjIndex.Keys.Where(k => k.Item1 == idx.Page && k.Item2 < idx.Version).ToList()) st.ObjIndex.Remove(old);
            if (!ReferenceEquals(st, _state)) return;
            st.Tab.Objects.Refresh(idx);
            RetryPendingObjPress();
            InvalidateVisual();
        });
    }

    // ---- 눌림 ----

    private bool HandleObjectPointerPressed(PointerPressedEventArgs e)
    {
        var st = _state; var lay = _layout;
        if (st is null || lay is null || !IsObjectTool(st.Tab.Tool)) return false;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        e.Handled = true;
        if (!st.Tab.CanEditContent) return true;
        var hit = lay.HitTest(e.GetPosition(this));
        if (hit is null) { st.Tab.Objects.Clear(); return true; }
        var press = new PressInfo(e.GetPosition(this), e.KeyModifiers, e.ClickCount, e.Pointer, hit.Value.Page);
        _pendingObjPress = null;
        if (!BeginObjectPress(st, press)) _pendingObjPress = press;   // 쪽 정보·객체 목록이 오면 다시
        return true;
    }

    private void RetryPendingObjPress()
    {
        if (_pendingObjPress is not { } p || _state is not { } st) return;
        if (BeginObjectPress(st, p)) _pendingObjPress = null;
    }

    private bool BeginObjectPress(ViewState st, PressInfo e)
    {
        var lay = _layout;
        if (lay is null || e.PageIndex >= _layoutPages.Length) return true;
        var info = _layoutPages[e.PageIndex];
        if (!TryGetGeom(st, info, out var g)) return false;
        var tab = st.Tab;
        var pt = ToPdf(g, e.PageIndex, e.Pos, lay.Scale);
        float ptPerDip = (float)(1.0 / lay.Scale);
        if (IsAddTool(tab.Tool)) return BeginAddToolPress(st, e, info, g, pt);
        if (!TryGetObjectIndex(st, info, out var idx)) return false;
        var ctl = tab.Objects;
        bool cycle = (e.Mods & KeyModifiers.Alt) != 0;

        // 이미 고른 객체의 손잡이·몸통이면 그대로 끌기
        if (!cycle && tab.Tool == ToolKind.EditObject && ctl.Selected is { } cur && cur.Page == info.Id && ctl.HitHandle(pt, ptPerDip) is var hh && hh != ObjectHandle.None)
        {
            StartObjectDrag(e.Pointer, hh, pt, e.PageIndex, g);
            return true;
        }
        Func<PageObjectEntry, bool>? filter = null;
        if (tab.Tool == ToolKind.EditText)
        {
            // 텍스트 편집 도구: 글자 객체와, 앱 글꼴로 넣어 화면에서는 경로인 줄만 고른다
            var appIds = st.Session.Content.For(info.Id).OfType<InsertAppTextEdit>().Select(a => a.Id).ToHashSet();
            filter = x => x.Type == PdfPageObjectType.Text || (x.Ref.IsInserted && appIds.Contains(x.Ref.InsertedId));
        }
        var pick = ctl.Pick(idx, pt, 4 * ptPerDip, cycle, filter);
        if (pick is null)
        {
            ctl.Clear();
            InvalidateVisual();
            if (tab.Tool == ToolKind.EditText) NoticeForMissedText(st, info, idx, pt, 4 * ptPerDip);
            return true;
        }
        ctl.Select(new SelectedObject(info.Id, pick.Ref, pick.Type, pick.Bounds));
        InvalidateVisual();
        if (tab.Tool == ToolKind.EditText) { _ = BeginTextEditAsync(st, e.PageIndex, g, info, pick); return true; }
        if (!cycle) StartObjectDrag(e.Pointer, ObjectHandle.Body, pt, e.PageIndex, g);   // 고르자마자 끌 수 있게
        return true;
    }

    private void StartObjectDrag(IPointer pointer, ObjectHandle h, PdfPointF pt, int page, PageGeom g)
    {
        _state!.Tab.Objects.BeginDrag(h, pt);
        _objDragging = true;
        _objPage = page;
        _objGeom = g;
        pointer.Capture(this);
    }

    // ---- 움직임·놓임 ----

    private bool HandleObjectPointerMoved(PointerEventArgs e)
    {
        if (HandleShapePointerMoved(e)) return true;
        if (!_objDragging || _state is not { } st || _layout is not { } lay || _objGeom is null) return false;
        var pt = ToPdf(_objGeom, _objPage, e.GetPosition(this), lay.Scale);
        st.Tab.Objects.Drag(pt, (e.KeyModifiers & KeyModifiers.Shift) != 0);
        InvalidateVisual();
        return true;
    }

    private bool HandleObjectPointerReleased(PointerReleasedEventArgs e)
    {
        if (_pendingObjPress is not null) { _pendingObjPress = null; return true; }
        if (HandleShapePointerReleased(e)) return true;
        if (!_objDragging || _state is not { } st) return false;
        _objDragging = false;
        e.Pointer.Capture(null);
        var sel = st.Tab.Objects.Selected;
        var m = st.Tab.Objects.EndDrag();
        InvalidateVisual();
        if (m is { } matrix && sel is not null)
            _ = RunObjectEditAsync(st, sel.Page, new TransformEdit(st.Session.Content.NewId(), sel.Ref, matrix));
        return true;
    }

    private async Task RunObjectEditAsync(ViewState st, PageId page, params ContentEdit[] edits)
    {
        try { await st.Tab.RunContentCommandAsync(new ContentEditCommand(page, edits)); }
        catch (Exception) { }   // 쪽이 그사이 지워짐 등: 아무것도 바뀌지 않는다
    }

    // ---- 키 ----

    private bool HandleObjectKey(KeyEventArgs e)
    {
        if (_state is not { } st || !IsObjectTool(st.Tab.Tool) || st.Tab.Objects.Selected is not { } sel) return false;
        if (e.Key == Key.Escape) { st.Tab.Objects.Clear(); InvalidateVisual(); e.Handled = true; return true; }
        if (!st.Tab.CanEditContent) return false;
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None)
        {
            st.Tab.Objects.Clear();
            _ = RunObjectEditAsync(st, sel.Page, new DeactivateEdit(st.Session.Content.NewId(), sel.Ref));
            e.Handled = true;
            return true;
        }
        float step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 10f : 1f;
        (float dx, float dy)? d = e.Key switch
        {
            Key.Left => (-step, 0f), Key.Right => (step, 0f), Key.Up => (0f, step), Key.Down => (0f, -step), _ => null,
        };
        if (d is not { } v || (e.KeyModifiers & ~KeyModifiers.Shift) != 0) return false;
        // 화면 위쪽 = PDF y 증가(회전 없는 쪽 기준). 회전된 쪽은 쪽 좌표 방향으로 움직인다(검증 필요: 회전 쪽에서의 체감).
        _ = RunObjectEditAsync(st, sel.Page, new TransformEdit(st.Session.Content.NewId(), sel.Ref, PdfMatrix.Translate(v.dx, v.dy)));
        e.Handled = true;
        return true;
    }

    // ---- 그리기 ----

    private void DrawObjectSelection(DrawingContext ctx, ViewState st, PageInfo info, Rect pr, double scale)
    {
        DrawShapePreview(ctx, info, pr, scale);
        var ctl = st.Tab.Objects;
        if (ctl.Selected is not { } s || s.Page != info.Id) return;
        if (!TryGetGeom(st, info, out var g)) return;
        var tr = new PageTransform(g.Bounds, g.Rotation, scale);
        var r = ctl.Preview ?? s.Bounds;
        var v = tr.ToView(RectangleF.FromLTRB(r.Left, r.Bottom, r.Right, r.Top));
        var box = new Rect(pr.X + v.X, pr.Y + v.Y, Math.Max(1, v.W), Math.Max(1, v.H));
        ctx.DrawRectangle(null, new Pen(SelAccent, 1, new DashStyle([4, 3], 0)), box);
        if (ctl.IsDragging) return;
        const double h = ObjectSelectionController.HandleDip;
        foreach (var c in new[] { box.TopLeft, box.TopRight, box.BottomLeft, box.BottomRight })
            ctx.DrawRectangle(Brushes.White, new Pen(SelAccent, 1), new Rect(c.X - h / 2, c.Y - h / 2, h, h));
    }
}
