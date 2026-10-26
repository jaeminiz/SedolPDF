using CommunityToolkit.Mvvm.ComponentModel;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.TextEdit;

namespace PdfEditor.App.Panels;

/// <summary>
/// 고른 쪽 객체의 속성(M4 Task 9, UI 스레드 전용): 위치·크기(mm), 새로 넣은 도형의 선 색·두께·채우기, 새로 넣은 텍스트(앱 글꼴 줄)의 크기·색.
/// 바꾸면 ContentEditCommand 하나(실행 취소 한 번). 원본 도형·글자의 스타일은 읽지 않으므로 위치·크기만 보인다(한계).
/// </summary>
public sealed partial class ObjectPropertiesViewModel : ObservableObject
{
    public const double PtToMm = 25.4 / 72.0;
    private readonly DocumentTabViewModel _tab;

    public ObjectPropertiesViewModel(DocumentTabViewModel tab)
    {
        _tab = tab;
        tab.Objects.Changed += Refresh;
        tab.ParagraphBox.Changed += Refresh;
        Refresh();
    }

    /// <summary>문단 편집 창이 열려 있다 — 상자 폭(mm) 칸을 보인다(M5 C13).</summary>
    public bool HasParagraph => _tab.ParagraphBox.Active;
    public double ParagraphWidthMm => _tab.ParagraphBox.WidthPt * PtToMm;

    /// <summary>상자 폭을 mm로 바꾼다(편집 창이 줄바꿈을 다시 맞춘다).</summary>
    public Task SetParagraphWidthAsync(double widthMm) => _tab.ParagraphBox.RequestWidthAsync(widthMm / PtToMm);

    public IReadOnlyList<PdfColor> Swatches => PdfEditor.App.Viewer.Annotations.ToolDefaults.Swatches;

    public bool HasSelection => _tab.Objects.All.Count == 1;
    public bool IsShape { get; private set; }
    public bool IsAppText { get; private set; }

    public double XMm { get; private set; }
    public double YMm { get; private set; }
    public double WidthMm { get; private set; }
    public double HeightMm { get; private set; }

    public PdfColor? Stroke { get; private set; }
    public float StrokeWidth { get; private set; }
    public PdfColor? Fill { get; private set; }
    public float TextSizePt { get; private set; }
    public PdfColor TextColor { get; private set; }

    public event Action? Updated;

    private void Refresh()
    {
        var sel = _tab.Objects.Selected;
        IsShape = IsAppText = false;
        if (sel is not null)
        {
            var b = sel.Bounds;
            XMm = b.Left * PtToMm; YMm = b.Bottom * PtToMm; WidthMm = b.Width * PtToMm; HeightMm = b.Height * PtToMm;
            if (sel.Ref.IsInserted && _tab.Session is { } s)
            {
                var edits = s.Content.For(sel.Page);
                if (edits.FirstOrDefault(e => e.Id == sel.Ref.InsertedId) is InsertShapeEdit sh)
                {
                    IsShape = true;
                    var style = edits.OfType<SetShapeStyleEdit>().LastOrDefault(e => e.Target == sel.Ref)?.Style ?? sh.Shape.Style;
                    Stroke = style.Stroke; StrokeWidth = style.Width; Fill = style.Fill;
                }
                else if (edits.FirstOrDefault(e => e.Id == sel.Ref.InsertedId) is InsertAppTextEdit at)
                {
                    IsAppText = true;
                    TextSizePt = at.Line.SizePt; TextColor = at.Line.Color;
                }
            }
        }
        OnPropertyChanged(string.Empty);
        Updated?.Invoke();
    }

    /// <summary>모두 mm. 위치·크기를 바꾼다(옛 경계 → 새 경계 행렬 하나). 너비·높이는 1pt 이상.</summary>
    public async Task SetBoundsAsync(double xMm, double yMm, double widthMm, double heightMm)
    {
        if (_tab.Objects.Selected is not { } sel || _tab.Session is not { } s) return;
        float x = (float)(xMm / PtToMm), y = (float)(yMm / PtToMm);
        float w = Math.Max(ObjectSelectionController.MinSizePt, (float)(widthMm / PtToMm)), h = Math.Max(ObjectSelectionController.MinSizePt, (float)(heightMm / PtToMm));
        var to = new PdfRectF(x, y, x + w, y + h);
        var from = sel.Bounds;
        if (Math.Abs(to.Left - from.Left) < 0.01f && Math.Abs(to.Bottom - from.Bottom) < 0.01f
            && Math.Abs(to.Width - from.Width) < 0.01f && Math.Abs(to.Height - from.Height) < 0.01f) return;
        await RunAsync(sel.Page, new TransformEdit(s.Content.NewId(), sel.Ref, ObjectSelectionController.MapRect(from, to)));
    }

    public async Task SetShapeStyleAsync(PdfColor? stroke, float width, PdfColor? fill)
    {
        if (!IsShape || _tab.Objects.Selected is not { } sel || _tab.Session is not { } s) return;
        var style = new ShapeStyle(stroke, Math.Clamp(width, 0.25f, 50f), fill);
        await RunAsync(sel.Page, new SetShapeStyleEdit(s.Content.NewId(), sel.Ref, style));
    }

    /// <summary>앱 글꼴 줄의 크기·색 바꾸기: 같은 글을 새 크기·색으로 다시 넣고 옛 줄을 끈다(실행 취소 한 번).</summary>
    public async Task SetTextStyleAsync(float? size, PdfColor? color)
    {
        if (!IsAppText || _tab.Objects.Selected is not { } sel || _tab.Session is not { } s || !_tab.CanEditContent) return;
        try
        {
            string text = await ObjectToolController.ReadTextAsync(_tab, sel.Page, sel.Ref) ?? "";
            var font = DocumentTabViewModel.Appearance?.Font;
            var plan = await s.Thread.InvokeAsync(() => s.IndexOf(sel.Page) < 0
                ? ReplacePlan.Refused(ReplaceRefusal.NotText)
                : TextEditPlanner.PlanReplace(s, sel.Page, sel.Ref, text, font, Math.Clamp(size ?? TextSizePt, 4f, 200f), color ?? TextColor));
            if (plan.Edits.Count == 0) { _tab.RaiseNotice(ObjectToolController.RefusalMessage(plan)); return; }
            await _tab.RunContentCommandAsync(new ContentEditCommand(sel.Page, plan.Edits));
            // 새 줄이 선택을 이어받는다
            if (plan.Edits.OfType<InsertAppTextEdit>().FirstOrDefault() is { } line)
                _tab.Objects.Select(new SelectedObject(sel.Page, ObjectRef.Inserted(line.Id), sel.Type, line.Line.Bounds));
        }
        catch (Exception) { _tab.RaiseNotice(Strings.Msg_ContentEditFailed); }
    }

    private async Task RunAsync(PageId page, ContentEdit edit)
    {
        try { await _tab.RunContentCommandAsync(new ContentEditCommand(page, [edit])); }
        catch (Exception) { _tab.RaiseNotice(Strings.Msg_ContentEditFailed); }
    }
}
