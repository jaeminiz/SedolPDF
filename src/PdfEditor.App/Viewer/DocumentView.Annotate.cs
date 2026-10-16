using RectangleF = System.Drawing.RectangleF;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 문서 화면의 주석 입력: 만들기 도구, 선택·이동·크기 조절, 지우개, 텍스트 마크업, 스티커 메모 편집 창.
/// 로직은 AnnotationToolController·SelectionController(Avalonia 없음)가 맡고, 여기는 좌표 변환과 입력 연결만 한다.
/// 실제 마우스·펜·터치 입력은 직접 확인한 것만 보고서에 적는다 (검증 필요).
/// </summary>
public sealed partial class DocumentView
{
    private enum Gesture { None, Create, Erase, Select }

    private Gesture _gesture;
    private int _gPage = -1;
    private PageId _gPageId;
    private PageGeom? _gGeom;
    private IPointer? _gPointer;
    private Point? _hover;   // 지우개 원 표시용 (문서 좌표)

    private const double EraserRadiusDip = 6;
    private static readonly IBrush SelAccent = new SolidColorBrush(Color.Parse("#005FB8"));

    private static bool IsTextMarkupTool(ToolKind t) => t is ToolKind.HighlightText or ToolKind.Underline or ToolKind.StrikeOut;
    private static bool UsesTextDrag(ToolKind t) => t == ToolKind.TextSelect || IsTextMarkupTool(t);
    private static bool IsAnnotationTool(ToolKind t) => t is not (ToolKind.Hand or ToolKind.TextSelect);

    private static Cursor CursorFor(ToolKind t) => new(t switch
    {
        ToolKind.Hand => StandardCursorType.Hand,
        ToolKind.AnnotSelect => StandardCursorType.Arrow,
        ToolKind.Pencil or ToolKind.Eraser or ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.HighlightArea or ToolKind.StickyNote
            => StandardCursorType.Cross,
        _ => StandardCursorType.Ibeam,
    });

    // ---- 좌표 ----

    private PdfPointF ToPdf(PageGeom g, int pageIndex, Point doc, double scale)
    {
        var origin = _layout!.GetPageRect(pageIndex).TopLeft;
        var tr = new PageTransform(g.Bounds, g.Rotation, scale);
        var (x, y) = tr.ToPage(new ViewPoint(doc.X - origin.X, doc.Y - origin.Y));
        return new PdfPointF((float)x, (float)y);
    }

    private PdfPointF? GesturePoint(PointerEventArgs e)
    {
        var lay = _layout;
        if (lay is null || _gGeom is null || _gPage < 0 || _gPage >= _layoutPages.Length || _layoutPages[_gPage].Id != _gPageId) return null;
        return ToPdf(_gGeom, _gPage, e.GetPosition(this), lay.Scale);
    }

    // ---- 눌림 ----

    /// <summary>주석 도구의 눌림. 처리했으면 true (텍스트 끌기 도구는 false를 돌려 M1 경로를 탄다).</summary>
    private bool HandleAnnotPointerPressed(PointerPressedEventArgs e)
    {
        var st = _state; var lay = _layout;
        if (st is null || lay is null) return false;
        var tab = st.Tab;
        var tool = tab.Tool;
        if (!IsAnnotationTool(tool) || IsTextMarkupTool(tool)) return false;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        e.Handled = true;
        if (!tab.CanAnnotate) return true;

        bool toggle = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0;
        var hit = lay.HitTest(e.GetPosition(this));
        if (hit is null)
        {
            if (tool == ToolKind.AnnotSelect && !toggle) tab.Selection.Clear();   // 쪽 밖에서 시작한 끌기는 무시
            return true;
        }
        var press = new PressInfo(e.GetPosition(this), e.KeyModifiers, e.ClickCount, e.Pointer, hit.Value.Page);
        if (!BeginPress(st, press)) _pendingPress = press;   // 쪽 정보가 도착하면 다시 시도한다
        return true;
    }

    private readonly record struct PressInfo(Point Pos, KeyModifiers Mods, int ClickCount, IPointer Pointer, int PageIndex);
    private PressInfo? _pendingPress;

    /// <summary>쪽 정보가 없어 미뤄 둔 눌림을, 정보가 도착한 뒤 (아직 누르고 있으면) 처음부터 다시 처리한다.</summary>
    private void RetryPendingPress()
    {
        if (_pendingPress is not { } p || _state is not { } st) return;
        if (!BeginPress(st, p)) return;   // 아직 없다: 다음 도착 때 다시
        _pendingPress = null;
    }

    /// <summary>쪽 정보가 없으면 false. 있으면 눌림을 처리하고 true.</summary>
    private bool BeginPress(ViewState st, PressInfo e)
    {
        var lay = _layout;
        if (lay is null || e.PageIndex >= _layoutPages.Length) return true;
        var tab = st.Tab;
        var tool = tab.Tool;
        var info = _layoutPages[e.PageIndex];
        if (!TryGetGeom(st, info, out var g)) return false;
        var hit = (Page: e.PageIndex, InPage: default(Point));
        bool toggle = (e.Mods & (KeyModifiers.Control | KeyModifiers.Shift)) != 0;
        var pt = ToPdf(g, e.PageIndex, e.Pos, lay.Scale);
        float ptPerDip = (float)(1.0 / lay.Scale);

        if (tool == ToolKind.AnnotSelect)
        {
            var annots = st.Session.Annotations.Get(info.Id);
            var h = tab.Editing.HitTest(annots, tab.Selection, pt, ptPerDip);
            if (h is null) { if (!toggle) tab.Selection.Clear(); return true; }
            if (toggle) { tab.Selection.Toggle(info.Id, h.Id); return true; }
            var data = annots.Items.FirstOrDefault(v => v.Data.Id == h.Id)?.Data;
            if (e.ClickCount >= 2 && h.Handle == HandleKind.Body && data?.Kind == AnnotKind.Text)
            {
                tab.Selection.Set(info.Id, [h.Id]);
                OpenStickyEditor(st, info.Id, h.Id);
                return true;
            }
            tab.Editing.Begin(annots, tab.Selection, h, pt, PdfRectF.FromLtrbRectangle(g.Bounds), ptPerDip);
            StartGesture(e.Pointer, Gesture.Select, hit.Page, info.Id, g);
            return true;
        }

        tab.Tools.Begin(tool, DocumentTabViewModel.StyleFor(tool), info.Id, pt, ptPerDip);
        StartGesture(e.Pointer, tool == ToolKind.Eraser ? Gesture.Erase : Gesture.Create, hit.Page, info.Id, g);
        PushPreview();
        return true;
    }

    private void StartGesture(IPointer pointer, Gesture kind, int page, PageId id, PageGeom g)
    {
        _gesture = kind; _gPage = page; _gPageId = id; _gGeom = g; _gPointer = pointer;
        pointer.Capture(this);
    }

    // ---- 움직임 ----

    private void HandleAnnotPointerMoved(PointerEventArgs e)
    {
        if (_state is { Tab.Tool: ToolKind.Eraser } && IsAnnotationHover(e))
        {
            _hover = e.GetPosition(this);
            InvalidateVisual();
        }
        else if (_hover is not null) { _hover = null; InvalidateVisual(); }
        if (_gesture == Gesture.None || _state is not { } st) return;
        if (GesturePoint(e) is not { } pt) { CancelGesture(); return; }
        if (_gesture == Gesture.Select) st.Tab.Editing.Move(pt); else st.Tab.Tools.Move(pt);
        PushPreview();
    }

    private bool IsAnnotationHover(PointerEventArgs e) => _layout?.HitTest(e.GetPosition(this)) is not null;

    private void PushPreview()
    {
        if (_state is not { } st) return;
        var tab = st.Tab;
        IReadOnlyDictionary<AnnotId, Annotation?>? items = _gesture switch
        {
            Gesture.Create => tab.Tools.Preview is { } p ? new Dictionary<AnnotId, Annotation?> { [p.Id] = p } : null,
            Gesture.Erase => tab.Tools.Previews,
            Gesture.Select => tab.Editing.Previews,
            _ => null,
        };
        SetAnnotationPreview(_gPageId, items);
    }

    // ---- 놓임 ----

    private bool HandleAnnotPointerReleased(PointerReleasedEventArgs e)
    {
        if (_pendingPress is not null) { _pendingPress = null; return true; }
        if (_gesture == Gesture.None) return false;
        var kind = _gesture;
        var st = _state;
        var pt = GesturePoint(e);
        _gesture = Gesture.None;
        e.Pointer.Capture(null);
        _gPointer = null;
        if (st is null || pt is not { } p)
        {
            st?.Tab.Tools.Cancel(); st?.Tab.Editing.Cancel();
            SetAnnotationPreview(null, null);
            return true;
        }
        var tab = st.Tab;
        var cmd = kind == Gesture.Select ? tab.Editing.End(p) : tab.Tools.End(p);
        if (cmd is null) { SetAnnotationPreview(null, null); return true; }
        bool sticky = kind == Gesture.Create && tab.Tool == ToolKind.StickyNote;
        var sticky0 = sticky ? tab.Tools.LastCreatedSticky : null;
        _ = RunAnnotationCommandAsync(st, cmd, _gPageId, sticky0);
        return true;
    }

    // 명령이 끝날 때까지 미리보기를 남겨 두어 깜빡이지 않게 한다 (Changed → 다시 그리기가 먼저 도착한다).
    private async Task RunAnnotationCommandAsync(ViewState st, PdfEditor.Document.Commands.IUndoableCommand cmd, PageId page, Annotation? newSticky)
    {
        try { await st.Tab.RunAnnotationCommandAsync(cmd); }
        catch (Exception) { st.Tab.RaiseNotice(Strings.Msg_AnnotationFailed); }
        finally
        {
            if (!st.Disposed && ReferenceEquals(st, _state) && _gesture == Gesture.None) SetAnnotationPreview(null, null);
        }
        if (newSticky is not null && !st.Disposed && ReferenceEquals(st, _state)) OpenStickyEditor(st, page, newSticky.Id);
    }

    private int _markupEpoch;
    private bool _markupPending;   // 놓은 뒤 FinishMarkupAsync가 끝나기를 기다리는 중

    /// <summary>진행 중이거나 끝나기를 기다리는 텍스트 마크업 끌기를 취소한다 (아무것도 만들지 않는다).</summary>
    private void CancelMarkup()
    {
        if (!_selecting && !_markupPending) return;   // 끌기 중이거나 놓은 뒤 끝나기를 기다리는 중일 때만
        _markupEpoch++;
        _markupPending = false;
        _selecting = false;
        _selPageIndex = -1;
        _charQueue.Clear();
        _state?.Tab.TextSelection.Clear();
    }

    private void CancelGesture()
    {
        _pendingPress = null;
        CancelMarkup();   // 현재 도구와 무관하게 (도구 전환 뒤에 불려도) 진행 중인 마크업을 취소한다
        if (_gesture == Gesture.None) return;
        _gesture = Gesture.None;
        _gPointer?.Capture(null);
        _gPointer = null;
        _state?.Tab.Tools.Cancel();
        _state?.Tab.Editing.Cancel();
        SetAnnotationPreview(null, null);
    }

    // ---- 텍스트 마크업 (M1 텍스트 선택 끌기를 그대로 쓰고, 놓을 때 주석으로 만든다) ----

    private async Task FinishMarkupAsync(ViewState st, ToolKind tool)
    {
        int epoch = _markupEpoch;
        _markupPending = true;
        try
        {
            // 글자 번호 조회가 끝나기를 잠깐 기다린다 (마지막 움직임까지 반영).
            for (int i = 0; i < 200 && (_charBusy || _charQueue.Count > 0); i++) await Task.Delay(10);
            if (epoch != _markupEpoch) return;   // Esc·도구 전환으로 취소됨
            _selecting = false;
            var tab = st.Tab;
            var sel = tab.TextSelection;
            if (st.Disposed || sel.Page is not { } page || sel.Range is not { } r) return;
            var session = st.Session;
            var rects = await session.Thread.InvokeAsync(() => session.IndexOf(page) < 0
                ? Array.Empty<RectangleF>()
                : session.GetLoadedText(page).GetRects(r.Start, r.Count));
            if (epoch != _markupEpoch) return;
            var cmd = tab.CanAnnotate
                ? tab.Tools.CreateMarkup(tool, DocumentTabViewModel.StyleFor(tool), page, [.. rects.Select(PdfRectF.FromLtrbRectangle)])
                : null;
            _markupPending = false;
            sel.Clear();
            if (cmd is not null) await RunAnnotationCommandAsync(st, cmd, page, null);
        }
        catch (Exception) { if (epoch == _markupEpoch) { _selecting = false; _markupPending = false; } }   // PDFium 스레드가 닫혔다
        finally { if (epoch == _markupEpoch) _markupPending = false; }
    }

    // ---- 키 ----

    private bool HandleAnnotKey(KeyEventArgs e)
    {
        if (_state is not { } st) return false;
        if (e is { Key: Key.Escape, KeyModifiers: KeyModifiers.None } && IsAnnotationTool(st.Tab.Tool))
        {
            CancelGesture();
            st.Tab.EscapePressed();
            e.Handled = true;
            return true;
        }
        if (e is { Key: Key.Delete, KeyModifiers: KeyModifiers.None } && st.Tab.Selection.Page is { } page && st.Tab.CanAnnotate)
        {
            var cmd = st.Tab.Editing.DeleteSelected(st.Session.Annotations.Get(page), st.Tab.Selection);
            if (cmd is not null)
            {
                st.Tab.Selection.Clear();
                _ = RunAnnotationCommandAsync(st, cmd, page, null);
            }
            e.Handled = true;
            return true;
        }
        return false;
    }

    // ---- 그리기: 선택 표시, 지우개 원 ----

    private void DrawSelection(DrawingContext ctx, ViewState st, PageInfo info, Rect pr, double scale)
    {
        var sel = st.Tab.Selection;
        if (sel.Page != info.Id || sel.Ids.Count == 0) return;
        if (!TryGetGeom(st, info, out var g)) return;
        var tr = new PageTransform(g.Bounds, g.Rotation, scale);
        var annots = st.Session.Annotations.Get(info.Id);
        var previews = _annotPreview is { } ap && ap.Page == info.Id ? ap.Items : NoPreviews;
        var pen = new Pen(SelAccent, 1, new DashStyle([4, 3], 0));
        foreach (var id in sel.Ids)
        {
            Annotation? a = previews.TryGetValue(id, out var pv) ? pv : annots.Items.FirstOrDefault(v => v.Data.Id == id)?.Data;
            if (a is null) continue;
            var r = AnnotationGeometry.ComputeRect(a);
            var v = tr.ToView(RectangleF.FromLTRB(r.Left, r.Bottom, r.Right, r.Top));
            ctx.DrawRectangle(null, pen, new Rect(pr.X + v.X, pr.Y + v.Y, v.W, v.H));
            if (sel.Ids.Count != 1 || !AnnotationGeometry.CanResize(a.Kind)) continue;
            float mx = (r.Left + r.Right) / 2, my = (r.Bottom + r.Top) / 2;
            foreach (var (x, y) in new[] { (r.Left, r.Top), (r.Right, r.Top), (r.Right, r.Bottom), (r.Left, r.Bottom),
                                           (mx, r.Top), (mx, r.Bottom), (r.Right, my), (r.Left, my) })
            {
                var c = tr.ToView(x, y);
                const double h = SelectionController.HandleDip;
                ctx.DrawRectangle(Brushes.White, new Pen(SelAccent, 1),
                    new Rect(pr.X + c.X - h / 2, pr.Y + c.Y - h / 2, h, h));
            }
        }
    }

    private void DrawEraserCursor(DrawingContext ctx)
    {
        if (_hover is not { } p || _state is not { Tab.Tool: ToolKind.Eraser }) return;
        ctx.DrawEllipse(null, new Pen(Brushes.White, 3), p, EraserRadiusDip, EraserRadiusDip);
        ctx.DrawEllipse(null, new Pen(Brushes.Black, 1), p, EraserRadiusDip, EraserRadiusDip);
    }

    // ---- 스티커 메모 편집 창 ----

    private Popup? _popup;
    private StickyNoteEditor? _editor;
    private ViewState? _editState;
    private PageId _editPage;
    private AnnotId _editId;
    private string _editOriginal = "";

    private void EnsureStickyPopup()
    {
        if (_popup is not null) return;
        _editor = new StickyNoteEditor();
        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Right,
            IsLightDismissEnabled = true,
            Child = _editor,
        };
        ((ISetLogicalParent)_popup).SetParent(this);
        LogicalChildren.Add(_popup);
        _popup.Closed += (_, _) => { if (!_popup.IsOpen) CommitSticky(); };
        _editor.AcceptRequested += () => _popup.IsOpen = false;
        _editor.CancelRequested += () => { _editState = null; _popup.IsOpen = false; };
    }

    private void OpenStickyEditor(ViewState st, PageId page, AnnotId id)
    {
        if (!st.Tab.CanAnnotate || _layout is not { } lay) return;
        var a = st.Session.Annotations.Get(page).Items.FirstOrDefault(v => v.Data.Id == id)?.Data;
        int idx = -1;
        for (int i = 0; i < _layoutPages.Length; i++) if (_layoutPages[i].Id == page) { idx = i; break; }
        if (a is null || idx < 0 || !TryGetGeom(st, _layoutPages[idx], out var g)) return;
        EnsureStickyPopup();
        CommitSticky();   // 열려 있던 편집(A)은 여기서 바로 확정한다. 팝업은 닫지 않고 B로 다시 쓴다.

        var r = AnnotationGeometry.ComputeRect(a);
        var v = new PageTransform(g.Bounds, g.Rotation, lay.Scale).ToView(RectangleF.FromLTRB(r.Left, r.Bottom, r.Right, r.Top));
        var pr = lay.GetPageRect(idx);
        _popup!.PlacementRect = new Rect(pr.X + v.X, pr.Y + v.Y, Math.Max(v.W, 1), Math.Max(v.H, 1));

        _editState = st; _editPage = page; _editId = id;
        _editOriginal = a.Contents ?? "";
        string when = (a.ModifiedAt ?? DateTimeOffset.Now).ToLocalTime().ToString("g");
        _editor!.Load(string.Format(Strings.Sticky_Header, a.Author ?? "", when), _editOriginal);
        _popup.IsOpen = true;
        Dispatcher.UIThread.Post(() => _editor.FocusBox(), DispatcherPriority.Input);
    }

    /// <summary>열려 있는 편집 창을 닫는다. commit이 false면 내용을 버린다.</summary>
    private void CloseStickyEditor(bool commit)
    {
        if (!commit) _editState = null; else CommitSticky();
        if (_popup is { IsOpen: true }) _popup.IsOpen = false;
    }

    // 한 번만 확정된다 (_editState를 먼저 비우므로 닫힘 이벤트가 늦게 와도 다시 적용되지 않는다). 내용이 바뀐 경우만 교체 명령을 낸다.
    private void CommitSticky()
    {
        var st = _editState;
        _editState = null;
        if (st is null || st.Disposed || _editor is null) return;
        string text = _editor.Text;
        if (text == _editOriginal) return;
        var cur = st.Session.Annotations.Get(_editPage).Items.FirstOrDefault(v => v.Data.Id == _editId)?.Data;
        if (cur is null) return;
        var after = cur with { Contents = text, ModifiedAt = DateTimeOffset.Now };
        _ = RunAnnotationCommandAsync(st, ChangeAnnotationsCommand.Replace(_editPage, [(cur, after)]), _editPage, null);
    }
}
