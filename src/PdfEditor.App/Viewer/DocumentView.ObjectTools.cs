using RectangleF = System.Drawing.RectangleF;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 문서 화면의 객체 도구 입력(M4 Task 8·9): 텍스트 편집(한 줄 편집 창), 텍스트 추가, 도형 넣기(끌어서).
/// 계산·문구는 ObjectToolController, 실제 변경은 ContentEditCommand. 실제 마우스·한글 IME 입력은 직접 확인한 것만 보고서에 적는다(검증 필요).
/// </summary>
public sealed partial class DocumentView
{
    private static bool IsAddShapeTool(ToolKind t) => t is ToolKind.AddShape or ToolKind.AddEllipse or ToolKind.AddLine;
    private static bool IsAddTool(ToolKind t) => t == ToolKind.AddText || IsAddShapeTool(t);

    private static PdfEditor.Document.Content.ShapeKind ShapeOf(ToolKind t) => t switch
    {
        ToolKind.AddEllipse => ShapeKind.Ellipse,
        ToolKind.AddLine => ShapeKind.Line,
        _ => ShapeKind.Rectangle,
    };

    // ---- 도형 끌기 ----

    private bool _shapeDragging;
    private int _shapePage = -1;
    private PageGeom? _shapeGeom;
    private PdfPointF _shapeA, _shapeB;
    private ToolKind _shapeTool;

    private void BeginShapeDrag(IPointer pointer, int pageIndex, PageGeom g, PdfPointF pt, ToolKind tool)
    {
        _shapeDragging = true;
        _shapePage = pageIndex;
        _shapeGeom = g;
        _shapeA = _shapeB = pt;
        _shapeTool = tool;
        pointer.Capture(this);
        InvalidateVisual();
    }

    private bool HandleShapePointerMoved(PointerEventArgs e)
    {
        if (!_shapeDragging || _layout is not { } lay || _shapeGeom is null) return false;
        _shapeB = ToPdf(_shapeGeom, _shapePage, e.GetPosition(this), lay.Scale);
        InvalidateVisual();
        return true;
    }

    private bool HandleShapePointerReleased(PointerReleasedEventArgs e)
    {
        if (!_shapeDragging) return false;
        _shapeDragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
        var st = _state;
        if (st is null || _layout is not { } lay || _shapeGeom is null || _shapePage >= _layoutPages.Length) return true;
        _shapeB = ToPdf(_shapeGeom, _shapePage, e.GetPosition(this), lay.Scale);
        if (st.Tab.Tool != _shapeTool) return true;   // 끄는 사이 도구가 바뀜
        var page = _layoutPages[_shapePage].Id;
        _ = RunToolAsync(st, ObjectToolController.AddShapeAsync(st.Tab, page, ShapeOf(_shapeTool), _shapeA, _shapeB));
        return true;
    }

    /// <summary>끄는 도형의 점선 미리보기(쪽 위, DrawObjectSelection에서 부른다).</summary>
    private void DrawShapePreview(DrawingContext ctx, PageInfo info, Rect pr, double scale)
    {
        if (!_shapeDragging || _shapeGeom is null || _shapePage < 0 || _shapePage >= _layoutPages.Length || _layoutPages[_shapePage].Id != info.Id) return;
        var tr = new PageTransform(_shapeGeom.Bounds, _shapeGeom.Rotation, scale);
        var va = tr.ToView(new RectangleF(_shapeA.X, _shapeA.Y, 0, 0));
        var vb = tr.ToView(new RectangleF(_shapeB.X, _shapeB.Y, 0, 0));
        var pen = new Pen(SelAccent, 1, new DashStyle([4, 3], 0));
        var p1 = new Point(pr.X + va.X, pr.Y + va.Y);
        var p2 = new Point(pr.X + vb.X, pr.Y + vb.Y);
        switch (ShapeOf(_shapeTool))
        {
            case ShapeKind.Line: ctx.DrawLine(pen, p1, p2); break;
            case ShapeKind.Ellipse: ctx.DrawEllipse(null, pen, new Rect(p1, p2).Normalize()); break;
            default: ctx.DrawRectangle(null, pen, new Rect(p1, p2).Normalize()); break;
        }
    }

    // ---- 결과 표시 ----

    private async Task RunToolAsync(ViewState st, Task<ObjectToolResult> work)
    {
        ObjectToolResult r;
        try { r = await work; }
        catch (Exception) { r = new ObjectToolResult(Strings.Msg_ContentEditFailed, null); }
        if (st.Disposed) return;
        if (r.Notice is not null) st.Tab.RaiseNotice(r.Notice);
        if (r.Toast is not null && ReferenceEquals(st, _state)) ShowToast(r.Toast);
    }

    // ---- 텍스트 추가·편집 눌림 (BeginObjectPress에서 부른다) ----

    private bool BeginAddToolPress(ViewState st, PressInfo e, PageInfo info, PageGeom g, PdfPointF pt)
    {
        var tab = st.Tab;
        if (tab.Tool == ToolKind.AddText)
        {
            var lay = _layout!;
            var pr = lay.GetPageRect(e.PageIndex);
            double sizeDip = ObjectToolController.DefaultTextSizePt * lay.Scale;
            var at = new Rect(e.Pos.X, e.Pos.Y, 1, 1);
            int quarter = g.Rotation;
            var page = info.Id;
            OpenLineEditor(st, at, "", sizeDip, 120, async text =>
            {
                if (st.Disposed) return;
                await RunToolAsync(st, ObjectToolController.AddTextAsync(tab, page, pt, quarter, text));
            });
            return true;
        }
        BeginShapeDrag(e.Pointer, e.PageIndex, g, pt, tab.Tool);
        return true;
    }

    private async Task BeginTextEditAsync(ViewState st, int pageIndex, PageGeom g, PageInfo info, PageObjectEntry pick)
    {
        var tab = st.Tab;
        string original = (await ObjectToolController.ReadTextAsync(tab, info.Id, pick.Ref) ?? pick.TextPreview ?? "")
            .Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').TrimEnd();
        if (st.Disposed || !ReferenceEquals(st, _state) || _layout is not { } lay || tab.Tool != ToolKind.EditText) return;
        var tr = new PageTransform(g.Bounds, g.Rotation, lay.Scale);
        var b = pick.Bounds;
        var v = tr.ToView(RectangleF.FromLTRB(b.Left, b.Bottom, b.Right, b.Top));
        var pr = lay.GetPageRect(pageIndex);
        var at = new Rect(pr.X + v.X, pr.Y + v.Y, Math.Max(v.W, 1), Math.Max(v.H, 1));
        double sizeDip = Math.Clamp(v.H * 0.8, 8, 64);
        var page = info.Id;
        OpenLineEditor(st, at, original, sizeDip, v.W, async text =>
        {
            if (st.Disposed || text == original) return;
            await RunToolAsync(st, ObjectToolController.ReplaceTextAsync(tab, page, pick.Ref, text));
        });
    }

    /// <summary>편집할 글을 못 고른 눌림에 대한 안내: 묶음(폼) 안이면 그 사실을, 쪽에 편집할 글이 하나도 없으면 그 사실을 알린다(C10·C11).</summary>
    private void NoticeForMissedText(ViewState st, PageInfo info, PageObjectIndex idx, PdfPointF pt, float tolerancePt)
    {
        var appIds = st.Session.Content.For(info.Id).OfType<InsertAppTextEdit>().Select(a => a.Id).ToHashSet();
        bool IsText(PageObjectEntry x) => x.Type == PdfPageObjectType.Text || (x.Ref.IsInserted && appIds.Contains(x.Ref.InsertedId));
        if (idx.HitTest(pt, tolerancePt, x => x.Type == PdfPageObjectType.Form).Count > 0) st.Tab.RaiseNotice(Strings.Msg_FormChildNotice);
        else if (!idx.Entries.Any(IsText)) st.Tab.RaiseNotice(Strings.Msg_NoEditableText);
    }

    // ---- 한 줄 편집 창 ----

    private Popup? _lnPopup;
    private FreeTextEditor? _lnEditor;
    private Func<string, Task>? _lnCommit;
    private ViewState? _lnState;

    public bool IsLineEditorOpen => _lnPopup is { IsOpen: true };
    public FreeTextEditor? LineEditorForTest => _lnEditor;
    public string? ToastForTest => _toast;

    private void EnsureLinePopup()
    {
        if (_lnPopup is not null) return;
        _lnEditor = new FreeTextEditor();
        _lnPopup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
            IsLightDismissEnabled = true,   // 바깥 클릭 = 확정
            Child = _lnEditor,
        };
        ((ISetLogicalParent)_lnPopup).SetParent(this);
        LogicalChildren.Add(_lnPopup);
        _lnPopup.Closed += (_, _) => { if (!_lnPopup.IsOpen) FinishLineEdit(); };
        _lnEditor.AcceptRequested += () => _lnPopup.IsOpen = false;
        _lnEditor.CancelRequested += () => { _lnCommit = null; _lnPopup.IsOpen = false; };
    }

    /// <summary>한 줄 편집 창을 연다(viewRect: 문서 좌표). 확정하면 commit(입력한 글)을 한 번 부른다. 열려 있던 편집은 먼저 확정한다.</summary>
    private void OpenLineEditor(ViewState st, Rect viewRect, string text, double fontSizeDip, double minWidthDip, Func<string, Task> commit)
    {
        EnsureLinePopup();
        FinishLineEdit();
        _lnPopup!.PlacementRect = viewRect;
        _lnCommit = commit;
        _lnState = st;
        _lnEditor!.LoadLine(text, fontSizeDip, minWidthDip, Colors.Black);
        _lnPopup.IsOpen = true;
        Dispatcher.UIThread.Post(() => _lnEditor.FocusBox(), DispatcherPriority.Input);
    }

    private void CloseLineEditor(bool commit, ViewState? onlyFor = null)
    {
        if (onlyFor is not null && !ReferenceEquals(onlyFor, _lnState)) return;
        if (!commit) _lnCommit = null;
        if (_lnPopup is { IsOpen: true }) _lnPopup.IsOpen = false;
        else if (commit) FinishLineEdit();
    }

    // 한 번만 확정된다(상태를 먼저 비우므로 닫힘 이벤트가 늦게 와도 다시 적용되지 않는다).
    private void FinishLineEdit()
    {
        var commit = _lnCommit;
        _lnCommit = null;
        if (commit is null || _lnEditor is null) return;
        string text = _lnEditor.Text;
        _ = commit(text);
    }
}
