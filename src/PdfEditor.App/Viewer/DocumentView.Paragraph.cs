using RectangleF = System.Drawing.RectangleF;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using Rect = Avalonia.Rect;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 문단 편집 화면(M5 Task 8): 텍스트 편집 도구로 줄을 누르면 문단을 추정해 점선 문단 상자 + 여러 줄 편집 창을 연다.
/// 입력하는 동안 줄 배치를 50 ms 디바운스로 미리 계획해 "줄 N/최대 M"를 보이고, 쪽 아래를 넘으면 확정을 막는다.
/// 확정은 Ctrl+Enter·확정 단추·창 밖 클릭, 취소는 Esc. 계산은 ParagraphController(Avalonia 없음), 실제 변경은 ParagraphEditCommand 하나.
/// 실제 마우스·한글 IME 입력은 직접 확인한 것만 보고서에 적는다(검증 필요).
/// </summary>
public sealed partial class DocumentView
{
    private static readonly IBrush OverlapBrush = new SolidColorBrush(Color.Parse("#E07B00"));
    private static readonly IBrush BlockedBrush = new SolidColorBrush(Color.Parse("#D13438"));

    private Popup? _paraPopup;
    private ParagraphEditorPopup? _paraEditor;
    private ParagraphSession? _para;
    private ViewState? _paraSt;
    private int _paraPageIndex;
    private ParagraphPreview _paraPreview = ParagraphPreview.Empty;
    private int _paraVersion;
    private int _paraDebounce;
    private bool _paraPending;
    private bool _paraBusy, _paraCommitting, _paraHooked;
    private TopLevel? _paraTop;

    public bool IsParagraphEditorOpen => _paraPopup is { IsOpen: true } && _para is not null;
    public ParagraphEditorPopup? ParagraphEditorForTest => _paraEditor;
    public ParagraphBoxState ParagraphBoxStateForTest => _paraPreview.State;
    public ParagraphPreview ParagraphPreviewForTest => _paraPreview;
    public ParagraphSession? ParagraphSessionForTest => _para;
    /// <summary>미리 계획이 끝나 화면 표시가 최신인가(시험용).</summary>
    public bool ParagraphPreviewIdleForTest => !_paraBusy && !_paraPending;

    // ---- 시작 ----

    private async Task BeginTextOrParagraphEditAsync(ViewState st, int pageIndex, PageGeom g, PageInfo info, PageObjectEntry pick, PageObjectIndex idx)
    {
        var start = await ParagraphController.StartAsync(st.Tab, info.Id, pick, idx);
        if (st.Disposed || !ReferenceEquals(st, _state) || st.Tab.Tool != ToolKind.EditText) return;
        if (start.InForm) { st.Tab.RaiseNotice(Strings.Msg_FormChildNotice); return; }
        if (start.Session is { } ps) { OpenParagraphEditor(st, pageIndex, g, ps); return; }
        if (start.Reason is not null) ShowToast(start.Reason);
        await BeginTextEditAsync(st, pageIndex, g, info, pick);
    }

    private void EnsureParagraphPopup()
    {
        if (_paraPopup is not null) return;
        _paraEditor = new ParagraphEditorPopup();
        _paraPopup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
            IsLightDismissEnabled = false,   // 창 밖 클릭의 확정은 직접 처리한다(폭 손잡이·단추가 창 안에 있고, 넘치면 열어 둬야 한다)
            Child = _paraEditor,
        };
        ((ISetLogicalParent)_paraPopup).SetParent(this);
        LogicalChildren.Add(_paraPopup);
        _paraEditor.AcceptRequested += () => _ = TryAcceptParagraphAsync();
        _paraEditor.CancelRequested += () => CloseParagraphEditor();
        _paraEditor.TextEdited += () => SchedulePreview(delayed: true);
        _paraEditor.ExtendRequested += OnParagraphExtend;
        _paraEditor.WidthDragged += OnParagraphWidthDragged;
    }

    private void OpenParagraphEditor(ViewState st, int pageIndex, PageGeom g, ParagraphSession ps)
    {
        EnsureParagraphPopup();
        CloseLineEditor(commit: true);
        CloseParagraphEditor();
        _para = ps;
        _paraSt = st;
        _paraPageIndex = pageIndex;
        _paraPreview = ParagraphPreview.Empty;
        if (_layout is not { } lay) return;
        _paraEditor!.Load(ps.InitialText, ps.SizePt * lay.Scale, 60, 0, ps.CanExtend);
        _paraEditor.SetStatus(Strings.Para_Planning, ParagraphBoxState.Normal, canCommit: true);
        PlaceParagraphPopup(g);
        HookParagraphOutsideClick();
        st.Tab.ParagraphBox.Set(true, ps.WidthPt, ApplyPanelWidthAsync);
        _paraPopup!.IsOpen = true;
        Dispatcher.UIThread.Post(() => _paraEditor.FocusBox(), DispatcherPriority.Input);
        SchedulePreview(delayed: false);
        InvalidateVisual();
    }

    /// <summary>문단 상자(화면 좌표)에 맞춰 편집 창의 위치와 폭을 정한다. 줄 포함/제외 단추 줄이 위에 붙으므로 그 높이만큼 올린다.</summary>
    private void PlaceParagraphPopup(PageGeom g)
    {
        if (_para is not { } ps || _layout is not { } lay || _paraPopup is null || _paraEditor is null || _paraPageIndex >= _layoutPages.Length) return;
        var tr = new PageTransform(g.Bounds, g.Rotation, lay.Scale);
        var b = ps.Model.Box;
        var v = tr.ToView(RectangleF.FromLTRB(b.Left, b.Bottom, b.Right, b.Top));
        var pr = lay.GetPageRect(_paraPageIndex);
        var at = new Rect(pr.X + v.X, pr.Y + v.Y, Math.Max(v.W, 1), Math.Max(v.H, 1));
        _paraPopup.PlacementRect = at;
        _paraPopup.VerticalOffset = ps.CanExtend ? -26 : 0;
        // 쪽이 돌아 있어 상자가 화면에서 좁고 길쭉하면(예: 60 DIP) 글이 몇 글자씩 끊겨 읽을 수 없으므로 읽을 수 있는 폭을 보장한다
        _paraEditor.SetWidth(v.W + 6 < ParagraphEditorPopup.ReadableMinWidth ? ParagraphEditorPopup.ReadableMinWidth : v.W + 6);
    }

    private bool TryParagraphGeom(out PageGeom g)
    {
        g = null!;
        return _paraSt is { } st && _paraPageIndex < _layoutPages.Length && TryGetGeom(st, _layoutPages[_paraPageIndex], out g!);
    }

    // ---- 미리 계획 (50 ms 디바운스, PDFium 스레드) ----

    private void SchedulePreview(bool delayed)
    {
        if (_para is null) return;
        int token = ++_paraDebounce;
        _paraPending = true;
        if (!delayed) { _ = RunPreviewAsync(); return; }
        _ = Task.Delay(50).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (token == _paraDebounce) _ = RunPreviewAsync();
        }), TaskScheduler.Default);
    }

    private async Task RunPreviewAsync()
    {
        if (_para is not { } ps || _paraSt is not { } st || _paraEditor is null) return;
        int version = ++_paraVersion;
        _paraPending = false;
        _paraBusy = true;
        ParagraphPreview p;
        try { p = await ParagraphController.PreviewAsync(st.Tab, ps, _paraEditor.Text); }
        finally { if (version == _paraVersion) _paraBusy = false; }
        if (version != _paraVersion || !ReferenceEquals(ps, _para) || st.Disposed) return;
        _paraPreview = p;
        _paraEditor.SetStatus(p.StatusText, p.State, p.CanCommit, p.PlannedLines);
        InvalidateVisual();
    }

    // ---- 확정·취소·닫기 ----

    /// <summary>글을 확정한다. 쪽 아래를 넘는 등 확정할 수 없으면 편집 창을 열어 둔 채 이유를 보인다. 닫히면 true.</summary>
    private async Task<bool> TryAcceptParagraphAsync()
    {
        if (_para is not { } ps || _paraSt is not { } st || _paraEditor is null || _paraCommitting) return false;
        _paraCommitting = true;
        try
        {
            var r = await ParagraphController.CommitAsync(st.Tab, ps, _paraEditor.Text);
            if (!ReferenceEquals(ps, _para)) return true;
            if (!r.Committed)
            {
                _paraVersion++;   // 늦게 오는 미리 계획이 이 결과를 덮지 않게
                _paraPreview = r.Preview;
                _paraEditor.SetStatus(r.Preview.StatusText, r.Preview.State, r.Preview.CanCommit, r.Preview.PlannedLines);
                InvalidateVisual();
                return false;
            }
            st.Tab.Objects.Clear();
            CloseParagraphEditor();
            if (r.Toast is not null && ReferenceEquals(st, _state)) ShowToast(r.Toast);
            return true;
        }
        catch (Exception)
        {
            st.Tab.RaiseNotice(Strings.Msg_ParagraphCommitFailed);
            CloseParagraphEditor();
            return true;
        }
        finally { _paraCommitting = false; }
    }

    private void CloseParagraphEditor()
    {
        if (_para is null && _paraPopup is not { IsOpen: true }) return;
        _paraDebounce++;
        _paraPending = false;
        _paraVersion++;
        var st = _paraSt;
        _para = null;
        _paraSt = null;
        _paraPreview = ParagraphPreview.Empty;
        if (_paraPopup is { IsOpen: true }) _paraPopup.IsOpen = false;
        st?.Tab.ParagraphBox.Set(false, 0, null);
        InvalidateVisual();
    }

    // 창 밖 클릭 = 확정(한 번 누른 것은 삼킨다 — M4 한 줄 편집 창의 가벼운 닫기와 같다). 넘쳐서 못 닫으면 창을 열어 둔다.
    private void HookParagraphOutsideClick()
    {
        if (_paraHooked || TopLevel.GetTopLevel(this) is not { } top) return;
        _paraTop = top;
        top.AddHandler(PointerPressedEvent, OnParagraphOutsidePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _paraHooked = true;
    }

    private void OnParagraphOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_para is null || !IsParagraphEditorOpen) return;
        e.Handled = true;
        _ = TryAcceptParagraphAsync();
    }

    // ---- 줄 포함/제외, 폭 조절 ----

    private void OnParagraphExtend(int top, int bottom)
    {
        if (_para is not { } ps || _paraEditor is null || _paraSt is null || _layout is not { } lay || !TryParagraphGeom(out var g)) return;
        var next = ps.Extended(top, bottom);
        _para = next;
        _paraEditor.Load(next.InitialText, next.SizePt * lay.Scale, 60, 0, next.CanExtend);
        PlaceParagraphPopup(g);
        _paraSt.Tab.ParagraphBox.Set(true, next.WidthPt, ApplyPanelWidthAsync);
        SchedulePreview(delayed: false);
        InvalidateVisual();
    }

    private void OnParagraphWidthDragged(double deltaDip, bool leading)
    {
        if (_para is null || _layout is not { } lay) return;
        double dPt = deltaDip / lay.Scale;
        ApplyParagraphWidth(_para.WidthPt + (leading ? -dPt : dPt), moveStart: leading);
    }

    private Task ApplyPanelWidthAsync(double widthPt)
    {
        ApplyParagraphWidth(widthPt, moveStart: false);
        return Task.CompletedTask;
    }

    /// <summary>상자 폭을 바꾸고 줄바꿈을 다시 맞춘다. 왼쪽 손잡이는 글이 위를 향해 똑바로 선 쪽(회전 0°)에서만 시작 위치를 같이 옮긴다.</summary>
    private void ApplyParagraphWidth(double newWidthPt, bool moveStart)
    {
        if (_para is not { } ps || _paraEditor is null || _paraSt is null || !TryParagraphGeom(out var g)) return;
        bool upright = ((ps.Angle / 90) + g.Rotation) % 4 == 0;
        var next = ps.WithWidth(newWidthPt, moveStart && upright);
        _para = next;
        PlaceParagraphPopup(g);
        _paraSt.Tab.ParagraphBox.Set(true, next.WidthPt, ApplyPanelWidthAsync);
        SchedulePreview(delayed: false);
        InvalidateVisual();
    }

    /// <summary>배율이 바뀌었다: 편집 창의 글자 크기·위치·폭을 새 배율에 맞춘다(글과 상자는 그대로).</summary>
    private void OnParagraphViewChanged()
    {
        if (_para is not { } ps || _paraEditor is null || _layout is not { } lay || !TryParagraphGeom(out var g)) return;
        _paraEditor.SetFontSize(ps.SizePt * lay.Scale);
        PlaceParagraphPopup(g);
        InvalidateVisual();
    }

    // ---- 그리기 ----

    private void DrawParagraphBox(DrawingContext ctx, ViewState st, PageInfo info, Rect pr, double scale)
    {
        if (_para is not { } ps || !ReferenceEquals(st, _paraSt) || ps.Page != info.Id || !TryGetGeom(st, info, out var g)) return;
        var tr = new PageTransform(g.Bounds, g.Rotation, scale);
        var b = (_paraPreview.DisplayBox ?? ps.Model.Box).Inflate(2f);
        var v = tr.ToView(RectangleF.FromLTRB(b.Left, b.Bottom, b.Right, b.Top));
        IBrush brush = _paraPreview.State switch { ParagraphBoxState.Blocked => BlockedBrush, ParagraphBoxState.Overlap => OverlapBrush, _ => SelAccent };
        ctx.DrawRectangle(null, new Pen(brush, 1.5, new DashStyle([4, 3], 0)), new Rect(pr.X + v.X, pr.Y + v.Y, Math.Max(1, v.W), Math.Max(1, v.H)));
    }
}
