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

    /// <summary>고른 글 객체(원본·앱 글꼴 줄)에서 읽은 글자 모양. 읽기 전이거나 글이 아니면 null.</summary>
    public TextFormatInfo? Format { get; private set; }
    public bool HasTextFormat => Format is not null;

    public event Action? Updated;

    private void Refresh()
    {
        var sel = _tab.Objects.Selected;
        IsShape = IsAppText = false;
        Format = null;
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
        if (sel is not null && HasSelection && (IsAppText || sel.Type == PdfPageObjectType.Text)) _ = LoadFormatAsync(sel);
    }

    private int _formatVersion;

    /// <summary>선택한 글 객체의 글자 모양을 PDFium 스레드에서 읽어 패널에 알린다(늦게 온 결과가 다른 선택을 덮지 않게 번호로 거른다).</summary>
    private async Task LoadFormatAsync(SelectedObject sel)
    {
        int version = ++_formatVersion;
        if (_tab.Session is not { } s) return;
        TextFormatInfo? f;
        try
        {
            f = await s.Thread.InvokeAsync(() =>
            {
                if (s.IndexOf(sel.Page) < 0) return null;
                bool overlapped = !sel.Ref.IsInserted && TextBoxOverlay.HasOverlappedCopy(s.GetObjectIndex(sel.Page), sel.Ref, sel.Bounds);
                return TextFormatReader.Read(s, sel.Page, sel.Ref, overlapped);
            });
        }
        catch (Exception) { return; }
        if (version != _formatVersion || _tab.Objects.Selected is not { } cur || cur.Ref != sel.Ref) return;
        Format = f;
        if (f is not null) { TextSizePt = f.SizePt; TextColor = f.Color; }
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
        var map = ObjectSelectionController.MapRect(from, to);
        var copies = await CopiesOfAsync(sel);
        await RunManyAsync(sel.Page, new[] { sel.Ref }.Concat(copies).Select(r => (ContentEdit)new TransformEdit(s.Content.NewId(), r, map)));
    }

    public async Task SetShapeStyleAsync(PdfColor? stroke, float width, PdfColor? fill)
    {
        if (!IsShape || _tab.Objects.Selected is not { } sel || _tab.Session is not { } s) return;
        var style = new ShapeStyle(stroke, Math.Clamp(width, 0.25f, 50f), fill);
        await RunAsync(sel.Page, new SetShapeStyleEdit(s.Content.NewId(), sel.Ref, style));
    }

    // ---- 글자 형식 바꾸기 (0.6.0) ----

    /// <summary>글 크기(pt). 원본 글은 글꼴을 그대로 두고 줄 시작점(기준선) 기준으로 균등하게 키우거나 줄인다. 앱 글꼴 줄은 다시 넣는다.</summary>
    public async Task SetTextSizeAsync(float sizePt)
    {
        if (Format is not { } f || _tab.Objects.Selected is not { } sel || _tab.Session is not { } s || !_tab.CanEditContent) return;
        sizePt = Math.Clamp(sizePt, 4f, 200f);
        if (Math.Abs(sizePt - f.SizePt) < 0.05f) return;
        if (!f.IsOriginalFont) { await SetTextStyleAsync(sizePt, null); return; }
        try
        {
            float ratio = sizePt / Math.Max(f.SizePt, 0.1f);
            var m = await s.Thread.InvokeAsync(() => s.IndexOf(sel.Page) < 0 ? (PdfMatrix?)null : s.GetReplay(sel.Page).Resolve(sel.Ref)?.Matrix);
            if (m is not { } mat) return;
            var scale = PdfMatrix.ScaleAbout(ratio, ratio, mat.E, mat.F);
            var copies = await CopiesOfAsync(sel);
            await _tab.RunContentCommandAsync(new ContentEditCommand(sel.Page, [new TransformEdit(s.Content.NewId(), sel.Ref, scale), .. copies.Select(c => (ContentEdit)new TransformEdit(s.Content.NewId(), c, scale))]));
            var b = sel.Bounds;
            var pts = new[] { scale.Apply(new PdfPointF(b.Left, b.Bottom)), scale.Apply(new PdfPointF(b.Right, b.Top)) };
            _tab.Objects.Select(new SelectedObject(sel.Page, sel.Ref, sel.Type,
                new PdfRectF(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y))));
        }
        catch (Exception) { _tab.RaiseNotice(Strings.Msg_ContentEditFailed); }
    }

    /// <summary>글자 색. 원본 글은 글꼴을 그대로 두고 색만 바꾼다(SetTextColorEdit). 앱 글꼴 줄은 다시 넣는다.</summary>
    public async Task SetTextColorAsync(PdfColor color)
    {
        if (Format is not { } f || _tab.Objects.Selected is not { } sel || _tab.Session is not { } s || !_tab.CanEditContent) return;
        if (f.Color == color) return;
        if (!f.IsOriginalFont) { await SetTextStyleAsync(null, color); return; }
        var copies = await CopiesOfAsync(sel);   // 겹쳐 찍은 글(가짜 굵게)은 복사본도 같이 바꿔야 가장자리에 옛 색이 남지 않는다
        await RunManyAsync(sel.Page, new[] { sel.Ref }.Concat(copies).Select(r => (ContentEdit)new SetTextColorEdit(s.Content.NewId(), r, color)));
    }

    /// <summary>굵게·기울임·명조(null = 그대로). 원본 글꼴의 모양은 바꿀 수 없어서 앱 글꼴로 줄을 다시 쓰고 알린다.</summary>
    public async Task SetTextEmphasisAsync(bool? bold = null, bool? italic = null, bool? serif = null)
    {
        if (Format is not { } f || _tab.Objects.Selected is not { } sel || _tab.Session is not { } s || !_tab.CanEditContent) return;
        var format = new TextFormatOverride(bold, italic, serif);
        if (format.IsEmpty) return;
        if (!f.IsOriginalFont) { await SetTextStyleAsync(null, null, format); return; }
        try
        {
            string text = await ObjectToolController.ReadTextAsync(_tab, sel.Page, sel.Ref) ?? "";
            IReadOnlyList<ObjectRef> shadows = [];
            var pick = await s.Thread.InvokeAsync(() => s.IndexOf(sel.Page) < 0 ? null : s.GetObjectIndex(sel.Page) is { } idx && idx.Entries.FirstOrDefault(e => e.Ref == sel.Ref) is { } e ? (Entry: e, Idx: idx) : ((PageObjectEntry Entry, PageObjectIndex Idx)?)null);
            if (pick is { } p) shadows = (await ObjectToolController.ResolveOverlappedAsync(_tab, sel.Page, p.Entry, p.Idx)).Shadows;
            var r = await ObjectToolController.ReplaceTextAsync(_tab, sel.Page, sel.Ref, text, shadows, format);
            if (r.Notice is not null) _tab.RaiseNotice(r.Notice);
            _tab.Objects.Clear();
        }
        catch (Exception) { _tab.RaiseNotice(Strings.Msg_ContentEditFailed); }
    }

    /// <summary>앱 글꼴 줄의 크기·색 바꾸기: 같은 글을 새 크기·색으로 다시 넣고 옛 줄을 끈다(실행 취소 한 번).</summary>
    public async Task SetTextStyleAsync(float? size, PdfColor? color, TextFormatOverride? format = null)
    {
        if (!IsAppText || _tab.Objects.Selected is not { } sel || _tab.Session is not { } s || !_tab.CanEditContent) return;
        try
        {
            string text = await ObjectToolController.ReadTextAsync(_tab, sel.Page, sel.Ref) ?? "";
            var font = DocumentTabViewModel.Appearance?.Font;
            var plan = await s.Thread.InvokeAsync(() => s.IndexOf(sel.Page) < 0
                ? ReplacePlan.Refused(ReplaceRefusal.NotText)
                : TextEditPlanner.PlanReplace(s, sel.Page, sel.Ref, text, font, Math.Clamp(size ?? TextSizePt, 4f, 200f), color ?? TextColor, catalog: DocumentTabViewModel.Appearance?.Catalog, format: format));
            if (plan.Edits.Count == 0) { _tab.RaiseNotice(ObjectToolController.RefusalMessage(plan)); return; }
            await _tab.RunContentCommandAsync(new ContentEditCommand(sel.Page, plan.Edits));
            // 새 줄이 선택을 이어받는다
            if (plan.Edits.OfType<InsertAppTextEdit>().FirstOrDefault() is { } line)
                _tab.Objects.Select(new SelectedObject(sel.Page, ObjectRef.Inserted(line.Id), sel.Type, line.Line.Bounds));
        }
        catch (Exception) { _tab.RaiseNotice(Strings.Msg_ContentEditFailed); }
    }

    /// <summary>원본 글 객체와 같은 자리에 겹쳐 찍힌 복사본(가짜 굵게)들. 원본 글이 아니면 빈 목록.</summary>
    private async Task<IReadOnlyList<ObjectRef>> CopiesOfAsync(SelectedObject sel)
    {
        if (sel.Ref.IsInserted || _tab.Session is not { } s) return [];
        return await s.Thread.InvokeAsync(() => s.IndexOf(sel.Page) < 0 ? [] : TextBoxOverlay.CopiesOf(s.GetObjectIndex(sel.Page), sel.Ref, sel.Bounds));
    }

    private async Task RunManyAsync(PageId page, IEnumerable<ContentEdit> edits)
    {
        try { await _tab.RunContentCommandAsync(new ContentEditCommand(page, [.. edits])); }
        catch (Exception) { _tab.RaiseNotice(Strings.Msg_ContentEditFailed); }
    }

    private async Task RunAsync(PageId page, ContentEdit edit)
    {
        try { await _tab.RunContentCommandAsync(new ContentEditCommand(page, [edit])); }
        catch (Exception) { _tab.RaiseNotice(Strings.Msg_ContentEditFailed); }
    }
}
