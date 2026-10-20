using RectangleF = System.Drawing.RectangleF;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Appearance.Fonts;
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
    private bool _polyActive;   // 폴리라인·다각형이 점을 모으는 중 (눌림 사이에도 이어진다)
    private int _insertEpoch;   // 텍스트 삽입의 글자 조회가 도착하기 전에 도구·탭이 바뀌면 버린다

    private const double EraserRadiusDip = 6;
    private static readonly IBrush SelAccent = new SolidColorBrush(Color.Parse("#005FB8"));

    private static bool IsTextMarkupTool(ToolKind t) => t is ToolKind.HighlightText or ToolKind.Underline or ToolKind.StrikeOut or ToolKind.ReplaceText;
    private static bool UsesTextDrag(ToolKind t) => t == ToolKind.TextSelect || IsTextMarkupTool(t);
    private static bool IsAnnotationTool(ToolKind t) => t is not (ToolKind.Hand or ToolKind.TextSelect
        or ToolKind.EditObject or ToolKind.EditText or ToolKind.AddText or ToolKind.AddImage or ToolKind.AddShape or ToolKind.Snapshot);

    private static Cursor CursorFor(ToolKind t) => new(t switch
    {
        ToolKind.Hand => StandardCursorType.Hand,
        ToolKind.AnnotSelect or ToolKind.EditObject => StandardCursorType.Arrow,
        ToolKind.AddText or ToolKind.AddImage or ToolKind.AddShape or ToolKind.Snapshot => StandardCursorType.Cross,
        ToolKind.Pencil or ToolKind.Eraser or ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.HighlightArea or ToolKind.StickyNote
            or ToolKind.Line or ToolKind.Arrow or ToolKind.PolyLine or ToolKind.Polygon or ToolKind.Cloud or ToolKind.Callout
            or ToolKind.TextBox or ToolKind.Stamp or ToolKind.FileAttach
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
            if (toggle) { ToggleGroupSelection(tab, annots, info.Id, h.Id); return true; }
            var data = annots.Items.FirstOrDefault(v => v.Data.Id == h.Id)?.Data;
            if (e.ClickCount >= 2 && h.Handle == HandleKind.Body && data?.Kind is AnnotKind.Text or AnnotKind.Caret)
            {
                tab.Selection.Set(info.Id, SelectionController.ExpandGroups(annots, [h.Id]));
                OpenStickyEditor(st, info.Id, h.Id);
                return true;
            }
            if (e.ClickCount >= 2 && h.Handle == HandleKind.Body && data?.Kind == AnnotKind.FreeText)
            {
                tab.Selection.Set(info.Id, [h.Id]);
                OpenFreeTextEditor(st, info.Id, data, isNew: false);
                return true;
            }
            tab.Editing.Begin(annots, tab.Selection, h, pt, PdfRectF.FromLtrbRectangle(g.Bounds), ptPerDip);
            StartGesture(e.Pointer, Gesture.Select, hit.Page, info.Id, g);
            return true;
        }

        if (tool == ToolKind.InsertText)
        {
            // 클릭 한 번: 글자 경계 조회는 비동기라 눌림에서 바로 시작한다 (끌기 없음).
            _ = InsertTextAtAsync(st, info.Id, pt, ptPerDip);
            return true;
        }
        bool poly = tool is ToolKind.PolyLine or ToolKind.Polygon;
        if (poly && e.ClickCount >= 2 && tab.Tools.IsCollectingPoints)
        {
            FinishPoly(st);   // 더블클릭으로 끝낸다
            return true;
        }
        tab.Tools.PageBoundsOf = _ => PdfRectF.FromLtrbRectangle(g.Bounds);   // 스탬프 등이 쪽 밖으로 나가지 않게
        tab.Tools.PageRotationOf = _ => g.Rotation;   // 회전된 쪽: 글자가 있는 주석을 바로 세운다
        tab.Tools.Begin(tool, DocumentTabViewModel.StyleFor(tool), info.Id, pt, ptPerDip);
        _polyActive = poly && tab.Tools.IsCollectingPoints;
        StartGesture(e.Pointer, tool == ToolKind.Eraser ? Gesture.Erase : Gesture.Create, hit.Page, info.Id, g);
        PushPreview();
        return true;
    }

    // 선택 도구의 Ctrl/Shift 클릭: 묶음(텍스트 바꾸기)은 둘 다 더하거나 뺀다.
    private static void ToggleGroupSelection(DocumentTabViewModel tab, PageAnnotations annots, PageId page, AnnotId id)
    {
        var group = SelectionController.ExpandGroups(annots, [id]);
        var sel = tab.Selection;
        var next = sel.Page == page ? sel.Ids.ToList() : [];
        if (group.All(next.Contains)) next.RemoveAll(group.Contains);
        else foreach (var m in group) if (!next.Contains(m)) next.Add(m);
        sel.Set(page, next);
    }

    private void FinishPoly(ViewState st)
    {
        var cmd = st.Tab.Tools.FinishPolyline();
        _polyActive = false;
        SetAnnotationPreview(null, null);
        if (cmd is not null) _ = RunAnnotationCommandAsync(st, cmd, _gPageId, null);
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
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (_gesture == Gesture.None && _polyActive && _state is { } ps && ps.Tab.Tools.IsCollectingPoints)
        {
            if (GesturePoint(e) is { } hp) { ps.Tab.Tools.Move(hp, shift); PushPreview(); }   // 점 사이 고무줄 미리보기
            return;
        }
        if (_gesture == Gesture.None || _state is not { } st) return;
        if (GesturePoint(e) is not { } pt) { CancelGesture(); return; }
        if (_gesture == Gesture.Select) st.Tab.Editing.Move(pt); else st.Tab.Tools.Move(pt, shift);
        PushPreview();
    }

    private bool IsAnnotationHover(PointerEventArgs e) => _layout?.HitTest(e.GetPosition(this)) is not null;

    private void PushPreview()
    {
        if (_state is not { } st) return;
        var tab = st.Tab;
        var gesture = _gesture == Gesture.None && _polyActive ? Gesture.Create : _gesture;
        IReadOnlyDictionary<AnnotId, Annotation?>? items = gesture switch
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
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var cmd = kind == Gesture.Select ? tab.Editing.End(p) : tab.Tools.End(p, shift);
        if (cmd is null)
        {
            if (_polyActive && tab.Tools.IsCollectingPoints) PushPreview();   // 점을 계속 모은다
            else
            {
                _polyActive = false;
                SetAnnotationPreview(null, null);
                if (kind == Gesture.Create && tab.Tool == ToolKind.FileAttach && tab.Tools.PendingAttach is not null) _ = AttachFileAsync(st);
            }
            return true;
        }
        // 텍스트 상자·타자기·설명선: 글을 입력해 확정할 때 한 번에 만든다 (빈 내용이면 아무것도 만들지 않는다).
        if (kind == Gesture.Create && tab.Tools.LastCreatedEdit is { Kind: AnnotKind.FreeText } newText)
        {
            SetAnnotationPreview(null, null);
            OpenFreeTextEditor(st, _gPageId, newText, isNew: true);
            return true;
        }
        bool sticky = kind == Gesture.Create && tab.Tool == ToolKind.StickyNote;
        var sticky0 = sticky ? tab.Tools.LastCreatedSticky : null;
        _ = RunAnnotationCommandAsync(st, cmd, _gPageId, sticky0);
        return true;
    }

    // 명령이 끝날 때까지 미리보기를 남겨 두어 깜빡이지 않게 한다 (Changed → 다시 그리기가 먼저 도착한다).
    private async Task RunAnnotationCommandAsync(ViewState st, PdfEditor.Document.Commands.IUndoableCommand cmd, PageId page, Annotation? newEdit)
    {
        try { await st.Tab.RunAnnotationCommandAsync(cmd); }
        catch (Exception) { st.Tab.RaiseNotice(Strings.Msg_AnnotationFailed); }
        finally
        {
            if (!st.Disposed && ReferenceEquals(st, _state) && _gesture == Gesture.None) SetAnnotationPreview(null, null);
        }
        if (newEdit is not null && !st.Disposed && ReferenceEquals(st, _state)) OpenStickyEditor(st, page, newEdit.Id);
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
        _insertEpoch++;
        if (_polyActive)
        {
            _polyActive = false;
            _state?.Tab.Tools.Cancel();
            SetAnnotationPreview(null, null);
        }
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
            var rectList = rects.Select(PdfRectF.FromLtrbRectangle).ToList();
            var style = DocumentTabViewModel.StyleFor(tool);
            var cmd = !tab.CanAnnotate ? null
                : tool == ToolKind.ReplaceText ? tab.Tools.CreateReplace(style, page, rectList)
                : tab.Tools.CreateMarkup(tool, style, page, rectList);
            _markupPending = false;
            sel.Clear();
            // 텍스트 바꾸기: 묶음을 만든 뒤 끝 쪽 삽입 표시(Caret)의 내용 편집 창을 연다.
            if (cmd is not null) await RunAnnotationCommandAsync(st, cmd, page, tool == ToolKind.ReplaceText ? tab.Tools.LastCreatedEdit : null);
        }
        catch (Exception) { if (epoch == _markupEpoch) { _selecting = false; _markupPending = false; } }   // PDFium 스레드가 닫혔다
        finally { if (epoch == _markupEpoch) _markupPending = false; }
    }

    // ---- 키 ----

    private bool HandleAnnotKey(KeyEventArgs e)
    {
        if (_state is not { } st) return false;
        if (_polyActive && st.Tab.Tools.IsCollectingPoints && e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key == Key.Enter) { FinishPoly(st); e.Handled = true; return true; }
            if (e.Key == Key.Back)
            {
                st.Tab.Tools.RemoveLastPoint();
                if (!st.Tab.Tools.IsCollectingPoints) { _polyActive = false; SetAnnotationPreview(null, null); }
                else PushPreview();
                e.Handled = true;
                return true;
            }
        }
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
            if (sel.Ids.Count != 1) continue;
            // 손잡이 목록은 적중 판정(HitTest)과 같은 SelectionController.HandlesOf를 쓴다 (선 끝점·꼭짓점·상자 8개·설명선 끝점).
            foreach (var (_, _, hc) in SelectionController.HandlesOf(a))
            {
                var c = tr.ToView(hc.X, hc.Y);
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
    private Annotation? _editNew;   // 아직 문서에 없는 새 주석(텍스트 삽입 Caret): 내용을 입력해 확정할 때 한 번에 더한다

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
        _editor.CancelRequested += () => { _editState = null; _editNew = null; _popup.IsOpen = false; };
    }

    private void OpenStickyEditor(ViewState st, PageId page, AnnotId id, Annotation? pendingNew = null)
    {
        if (!st.Tab.CanAnnotate || _layout is not { } lay) return;
        var a = pendingNew ?? st.Session.Annotations.Get(page).Items.FirstOrDefault(v => v.Data.Id == id)?.Data;
        int idx = -1;
        for (int i = 0; i < _layoutPages.Length; i++) if (_layoutPages[i].Id == page) { idx = i; break; }
        if (a is null || idx < 0 || !TryGetGeom(st, _layoutPages[idx], out var g)) return;
        EnsureStickyPopup();
        CommitSticky();   // 열려 있던 편집(A)은 여기서 바로 확정한다. 팝업은 닫지 않고 B로 다시 쓴다.

        var r = AnnotationGeometry.ComputeRect(a);
        var v = new PageTransform(g.Bounds, g.Rotation, lay.Scale).ToView(RectangleF.FromLTRB(r.Left, r.Bottom, r.Right, r.Top));
        var pr = lay.GetPageRect(idx);
        _popup!.PlacementRect = new Rect(pr.X + v.X, pr.Y + v.Y, Math.Max(v.W, 1), Math.Max(v.H, 1));

        _editState = st; _editPage = page; _editId = id; _editNew = pendingNew;
        _editOriginal = a.Contents ?? "";
        string when = (a.ModifiedAt ?? DateTimeOffset.Now).ToLocalTime().ToString("g");
        _editor!.Load(string.Format(Strings.Sticky_Header, a.Author ?? "", when), _editOriginal);
        _popup.IsOpen = true;
        Dispatcher.UIThread.Post(() => _editor.FocusBox(), DispatcherPriority.Input);
    }

    /// <summary>열려 있는 편집 창을 닫는다. commit이 false면 내용을 버린다.</summary>
    private void CloseStickyEditor(bool commit)
    {
        if (!commit) { _editState = null; _editNew = null; } else CommitSticky();
        if (_popup is { IsOpen: true }) _popup.IsOpen = false;
    }

    // 한 번만 확정된다 (_editState를 먼저 비우므로 닫힘 이벤트가 늦게 와도 다시 적용되지 않는다). 내용이 바뀐 경우만 교체 명령을 낸다.
    private void CommitSticky()
    {
        var st = _editState;
        var pending = _editNew;
        _editState = null;
        _editNew = null;
        if (st is null || st.Disposed || _editor is null) return;
        string text = _editor.Text;
        if (text == _editOriginal) return;
        if (pending is not null)
        {
            if (text.Trim().Length == 0) return;   // 빈 내용이면 만들지 않는다
            var add = pending with { Contents = text, ModifiedAt = DateTimeOffset.Now };
            _ = RunAnnotationCommandAsync(st, ChangeAnnotationsCommand.Add(_editPage, add), _editPage, null);
            return;
        }
        var cur = st.Session.Annotations.Get(_editPage).Items.FirstOrDefault(v => v.Data.Id == _editId)?.Data;
        if (cur is null) return;
        var after = cur with { Contents = text, ModifiedAt = DateTimeOffset.Now };
        _ = RunAnnotationCommandAsync(st, ChangeAnnotationsCommand.Replace(_editPage, [(cur, after)]), _editPage, null);
    }

    // ---- 텍스트 상자·타자기·설명선 편집 창 (A14) ----

    private Popup? _ftPopup;
    private FreeTextEditor? _ftEditor;
    private ViewState? _ftState;
    private PageId _ftPage;
    private Annotation? _ftAnnot;
    private bool _ftIsNew;

    /// <summary>글 편집 창이 열려 있는가 (테스트용).</summary>
    internal bool IsFreeTextEditorOpen => _ftPopup is { IsOpen: true };
    internal FreeTextEditor? FreeTextEditorForTest => _ftEditor;

    private void EnsureFreeTextPopup()
    {
        if (_ftPopup is not null) return;
        _ftEditor = new FreeTextEditor();
        _ftPopup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
            IsLightDismissEnabled = true,   // 바깥 클릭 = 확정
            Child = _ftEditor,
        };
        ((ISetLogicalParent)_ftPopup).SetParent(this);
        LogicalChildren.Add(_ftPopup);
        _ftPopup.Closed += (_, _) => { if (!_ftPopup.IsOpen) FinishFreeTextEdit(); };
        _ftEditor.AcceptRequested += () => _ftPopup.IsOpen = false;
        _ftEditor.CancelRequested += () => { _ftState = null; _ftAnnot = null; _ftPopup.IsOpen = false; ClearEditPreview(); };
    }

    private void ClearEditPreview()
    {
        if (_gesture == Gesture.None && !_polyActive) SetAnnotationPreview(null, null);
    }

    /// <summary>
    /// 글 편집 창을 연다. isNew면 아직 문서에 없는 주석(내용을 입력해 확정할 때 한 번에 만든다, 빈 내용이면 만들지 않는다),
    /// 아니면 문서의 주석을 고친다(편집하는 동안 주석 층에서 숨긴다).
    /// </summary>
    private void OpenFreeTextEditor(ViewState st, PageId page, Annotation a, bool isNew)
    {
        if (!st.Tab.CanAnnotate || _layout is not { } lay) return;
        int idx = -1;
        for (int i = 0; i < _layoutPages.Length; i++) if (_layoutPages[i].Id == page) { idx = i; break; }
        if (idx < 0 || !TryGetGeom(st, _layoutPages[idx], out var g)) return;
        EnsureFreeTextPopup();
        FinishFreeTextEdit();   // 열려 있던 편집은 여기서 바로 확정한다
        CloseStickyEditor(commit: true);

        var box = a.Intent == FreeTextIntent.Callout ? a.TextBox ?? a.Rect : a.Rect;
        var v = new PageTransform(g.Bounds, g.Rotation, lay.Scale).ToView(RectangleF.FromLTRB(box.Left, box.Bottom, box.Right, box.Top));
        var pr = lay.GetPageRect(idx);
        _ftPopup!.PlacementRect = new Rect(pr.X + v.X, pr.Y + v.Y, Math.Max(v.W, 1), Math.Max(v.H, 1));

        _ftState = st; _ftPage = page; _ftAnnot = a; _ftIsNew = isNew;
        var c = a.TextColor ?? a.Color ?? new PdfColor(0, 0, 0);
        var align = a.Align switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left };
        _ftEditor!.Load(a.Contents ?? "", a.FontSizePt * lay.Scale, v.W, v.H, Color.FromRgb(c.R, c.G, c.B),
            wrap: a.Intent != FreeTextIntent.Typewriter, align);
        if (!isNew) SetAnnotationPreview(page, new Dictionary<AnnotId, Annotation?> { [a.Id] = null });
        _ftPopup.IsOpen = true;
        Dispatcher.UIThread.Post(() => _ftEditor.FocusBox(), DispatcherPriority.Input);
    }

    /// <summary>열려 있는 글 편집 창을 닫는다. commit이 false면 내용을 버린다.</summary>
    private void CloseFreeTextEditor(bool commit)
    {
        if (!commit) { _ftState = null; _ftAnnot = null; ClearEditPreview(); }
        else FinishFreeTextEdit();
        if (_ftPopup is { IsOpen: true }) _ftPopup.IsOpen = false;
    }

    // 한 번만 확정된다 (상태를 먼저 비우므로 닫힘 이벤트가 늦게 와도 다시 적용되지 않는다).
    private void FinishFreeTextEdit()
    {
        var st = _ftState;
        var orig = _ftAnnot;
        _ftState = null;
        _ftAnnot = null;
        if (st is null || orig is null || st.Disposed || _ftEditor is null) { ClearEditPreview(); return; }
        string text = _ftEditor.Text;
        try
        {
            var tools = st.Tab.Tools;
            var now = DateTimeOffset.Now;
            PdfEditor.Document.Commands.IUndoableCommand? cmd = null;
            if (_ftIsNew)
            {
                if (text.Trim().Length > 0)
                    cmd = ChangeAnnotationsCommand.Add(_ftPage, tools.FitForCommit(orig with { Contents = text, ModifiedAt = now }));
            }
            else
            {
                var cur = st.Session.Annotations.Get(_ftPage).Items.FirstOrDefault(v => v.Data.Id == orig.Id)?.Data;
                if (cur is not null && text != (cur.Contents ?? ""))
                    cmd = ChangeAnnotationsCommand.Replace(_ftPage, [(cur, tools.FitForCommit(cur with { Contents = text, ModifiedAt = now }))]);
            }
            if (cmd is null) { ClearEditPreview(); return; }
            WarnMissingGlyphs(st, text, orig.FontSizePt);
            _ = RunAnnotationCommandAsync(st, cmd, _ftPage, null);
        }
        catch (Exception)
        {
            // 글꼴이 없는 등으로 상자를 맞추지 못했다. Popup.Closed·탭 전환에서 불리므로 예외를 올리지 않는다.
            ClearEditPreview();
            st.Tab.RaiseNotice(Strings.Msg_AnnotationFailed);
        }
    }

    // 글꼴에 없는 글자가 있으면 한 번 알린다 (.notdef를 그리지 않고 건너뛴다).
    private static void WarnMissingGlyphs(ViewState st, string text, float sizePt)
    {
        try
        {
            if (DocumentTabViewModel.Appearance?.Font is not { } font) return;
            var laid = TextLayout.Layout(font, text, sizePt, float.PositiveInfinity, TextAlign.Left, new PdfPointF(0, 0));
            if (laid.MissingCodepoints.IsDefaultOrEmpty) return;
            st.Tab.RaiseNotice(string.Format(Strings.Msg_GlyphMissing, string.Join(" ", laid.MissingCodepoints.Select(char.ConvertFromUtf32))));
        }
        catch (Exception) { /* 안내만 생략한다 */ }
    }

    // ---- 텍스트 삽입 (클릭 → 글자 경계 조회 → Caret + 내용 편집 창) ----

    private async Task InsertTextAtAsync(ViewState st, PageId page, PdfPointF pt, float ptPerDip)
    {
        int epoch = ++_insertEpoch;
        var tab = st.Tab;
        if (!tab.CanAnnotate || !tab.CanUsePatchFeatures) return;
        var session = st.Session;
        float tol = 4f * ptPerDip;
        CharBoundary? at;
        try
        {
            at = await session.Thread.InvokeAsync(() =>
            {
                if (session.IndexOf(page) < 0) return (CharBoundary?)null;
                var text = session.GetLoadedText(page);
                int idx = text.GetCharIndexAtPos(pt.X, pt.Y, tol);
                if (idx < 0) return null;
                var rs = text.GetRects(idx, 1);
                if (rs.Count == 0) return null;
                var r = PdfRectF.FromLtrbRectangle(rs[0]);
                // 클릭이 글자의 왼쪽 절반이면 그 글자 앞, 오른쪽 절반이면 뒤
                float x = pt.X < (r.Left + r.Right) / 2f ? r.Left : r.Right;
                return new CharBoundary(x, r.Bottom, r.Height);
            });
        }
        catch (Exception) { return; }   // PDFium 스레드가 닫혔다
        if (epoch != _insertEpoch || st.Disposed || !ReferenceEquals(st, _state) || tab.Tool != ToolKind.InsertText || at is not { } b) return;
        if (tab.Tools.CreateInsertCaret(DocumentTabViewModel.StyleFor(ToolKind.InsertText), page, b) is null) return;
        if (tab.Tools.LastCreatedEdit is { } caret) OpenStickyEditor(st, page, caret.Id, pendingNew: caret);
    }

    // ---- 파일 첨부 (클릭 → 파일 열기 창 → 아이콘) ----

    private async Task AttachFileAsync(ViewState st)
    {
        var tab = st.Tab;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) { tab.Tools.CancelAttach(); return; }
            var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = Strings.Dlg_PickAttachTitle,
                AllowMultiple = false,
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) { tab.Tools.CancelAttach(); return; }
            if (st.Disposed || !ReferenceEquals(st, _state) || tab.Tool != ToolKind.FileAttach) { tab.Tools.CancelAttach(); return; }
            if (new FileInfo(path).Length > AnnotationAssets.MaxFileBytes) { tab.Tools.CancelAttach(); tab.RaiseNotice(Strings.Msg_AttachFailed); return; }
            byte[] data = await File.ReadAllBytesAsync(path);
            if (st.Disposed || !ReferenceEquals(st, _state) || tab.Tool != ToolKind.FileAttach) { tab.Tools.CancelAttach(); return; }
            var page = tab.Tools.PendingAttach?.Page;
            var cmd = tab.Tools.AttachFile(Path.GetFileName(path), data);
            if (cmd is null)
            {
                if (tab.Tools.LastAttachRejected) tab.RaiseNotice(Strings.Msg_AttachFailed);
                tab.Tools.CancelAttach();
                return;
            }
            await RunAnnotationCommandAsync(st, cmd, page ?? default, null);
        }
        catch (Exception)
        {
            tab.Tools.CancelAttach();
            tab.RaiseNotice(Strings.Msg_AttachFailed);   // 읽을 수 없는 파일 등: 알리고 끝낸다 (앱은 계속)
        }
    }
}
