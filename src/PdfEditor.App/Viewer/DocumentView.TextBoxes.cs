using RectangleF = System.Drawing.RectangleF;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document;
using Rect = Avalonia.Rect;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 텍스트 편집 도구를 켜면 고칠 수 있는 글 조각마다 옅은 상자를 미리 보인다(PDF-XChange·Acrobat처럼). 마우스를 올린 조각은 진하게.
/// 상자는 쪽 위 오버레이로만 그리고 쪽 그림(타일)은 건드리지 않는다. 계산은 TextBoxOverlay(Avalonia 없음).
/// </summary>
public sealed partial class DocumentView
{
    private static readonly IBrush TextBoxFaint = new SolidColorBrush(Color.FromArgb(0x70, 0x00, 0x5F, 0xB8));
    private static readonly IBrush TextBoxHoverFill = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x5F, 0xB8));

    private readonly Dictionary<(PageId Id, int Version), IReadOnlyList<PdfRectF>> _textBoxes = new();
    private (PageId Page, PdfRectF Box)? _textHover;

    private IReadOnlyList<PdfRectF>? TextBoxesOf(ViewState st, PageInfo info)
    {
        if (!TryGetObjectIndex(st, info, out var idx)) return null;
        var key = (info.Id, info.Version);
        if (_textBoxes.TryGetValue(key, out var cached)) return cached;
        if (_textBoxes.Count > 64) _textBoxes.Clear();
        return _textBoxes[key] = TextBoxOverlay.Boxes(idx, AppTextIds(st, info.Id));
    }

    /// <summary>마우스 아래 글 조각 강조를 갱신한다(텍스트 편집 도구일 때만).</summary>
    private void UpdateTextHover(PointerEventArgs e)
    {
        var old = _textHover;
        _textHover = null;
        if (_state is { } st && _layout is { } lay && st.Tab.Tool == ToolKind.EditText && st.Tab.CanEditContent
            && lay.HitTest(e.GetPosition(this)) is { } hit && hit.Page < _layoutPages.Length)
        {
            var info = _layoutPages[hit.Page];
            if (TryGetGeom(st, info, out var g) && TextBoxesOf(st, info) is { } boxes
                && TextBoxOverlay.HoverBox(boxes, ToPdf(g, hit.Page, e.GetPosition(this), lay.Scale), (float)(2 / lay.Scale)) is { } box)
                _textHover = (info.Id, box);
        }
        if (old != _textHover) InvalidateVisual();
    }

    private void DrawTextBoxes(DrawingContext ctx, ViewState st, PageInfo info, Rect pr, double scale)
    {
        if (st.Tab.Tool != ToolKind.EditText || !st.Tab.CanEditContent || !TryGetGeom(st, info, out var g) || TextBoxesOf(st, info) is not { } boxes) return;
        var tr = new PageTransform(g.Bounds, g.Rotation, scale);
        var faint = new Pen(TextBoxFaint, 1);
        foreach (var b in boxes)
        {
            var v = tr.ToView(RectangleF.FromLTRB(b.Left, b.Bottom, b.Right, b.Top));
            ctx.DrawRectangle(null, faint, new Rect(pr.X + v.X, pr.Y + v.Y, Math.Max(1, v.W), Math.Max(1, v.H)));
        }
        if (_textHover is { } h && h.Page == info.Id)
        {
            var v = tr.ToView(RectangleF.FromLTRB(h.Box.Left, h.Box.Bottom, h.Box.Right, h.Box.Top));
            ctx.DrawRectangle(TextBoxHoverFill, new Pen(SelAccent, 1.5), new Rect(pr.X + v.X, pr.Y + v.Y, Math.Max(1, v.W), Math.Max(1, v.H)));
        }
    }
}
