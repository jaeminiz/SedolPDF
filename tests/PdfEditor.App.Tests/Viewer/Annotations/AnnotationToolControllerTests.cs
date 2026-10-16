using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Annotations;

public class AnnotationToolControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly PageId Page = new(7);
    private static readonly ToolStyle Style = new(PdfColor.FromHex("#E5372D"), PdfColor.FromHex("#FFD60A"), 0.6f, 2f);
    private long _id;

    private AnnotationToolController Make()
        => new(() => new AnnotId(++_id), () => Now, () => "me", t => "subj-" + t);

    private static Annotation Added(ChangeAnnotationsCommand? cmd)
    {
        Assert.NotNull(cmd);
        var changes = (AnnotationChange[])typeof(ChangeAnnotationsCommand)
            .GetField("_changes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cmd)!;
        Assert.Single(changes);
        Assert.Null(changes[0].Before);
        return changes[0].After!;
    }

    private static void CheckCommon(Annotation a, ToolKind tool)
    {
        Assert.Equal("me", a.Author);
        Assert.Equal("subj-" + tool, a.Subject);
        Assert.Equal(Now, a.CreatedAt);
        Assert.Equal(Now, a.ModifiedAt);
        Assert.Equal(PdfAnnotFlags.Print, a.Flags);
        Assert.Equal(Style.Color, a.Color);
        Assert.Equal(Style.Opacity, a.Opacity);
    }

    [Fact]
    public void Rectangle_Drag_CreatesSquareWithStyle()
    {
        var c = Make();
        c.Begin(ToolKind.Rectangle, Style, Page, new(10, 10), 1f);
        c.Move(new(60, 30));
        Assert.NotNull(c.Preview);
        var a = Added(c.End(new(110, 60)));
        Assert.Equal(AnnotKind.Square, a.Kind);
        Assert.Equal(new PdfRectF(10, 10, 110, 60), a.Rect);
        Assert.Equal(2f, a.BorderWidth);
        Assert.Equal(Style.Fill, a.FillColor);
        CheckCommon(a, ToolKind.Rectangle);
        Assert.Null(c.Preview);
    }

    [Fact]
    public void TinyDrag_CreatesNothing()
    {
        foreach (var tool in new[] { ToolKind.Rectangle, ToolKind.Ellipse, ToolKind.HighlightArea })
        {
            var c = Make();
            c.Begin(tool, Style, Page, new(10, 10), 1f);
            Assert.Null(c.End(new(11, 11)));
        }
    }

    [Fact]
    public void Ellipse_Drag_CreatesCircle()
    {
        var c = Make();
        c.Begin(ToolKind.Ellipse, Style, Page, new(110, 60), 1f);
        var a = Added(c.End(new(10, 10)));
        Assert.Equal(AnnotKind.Circle, a.Kind);
        Assert.Equal(new PdfRectF(10, 10, 110, 60), a.Rect);
        CheckCommon(a, ToolKind.Ellipse);
    }

    [Fact]
    public void HighlightArea_Drag_HighlightWithOneRectQuad()
    {
        var c = Make();
        c.Begin(ToolKind.HighlightArea, Style, Page, new(10, 10), 1f);
        var a = Added(c.End(new(110, 60)));
        Assert.Equal(AnnotKind.Highlight, a.Kind);
        var q = Assert.Single(a.Quads);
        Assert.Equal(new PdfPointF(10, 60), q.P1);
        Assert.Equal(new PdfPointF(110, 10), q.P4);
        Assert.Equal(new PdfRectF(10, 10, 110, 60), a.Rect);
        CheckCommon(a, ToolKind.HighlightArea);
    }

    [Fact]
    public void Markup_TwoTextRects_TwoQuads()
    {
        var c = Make();
        var a = Added(c.CreateMarkup(ToolKind.Underline, Style, Page,
            [new PdfRectF(10, 10, 50, 20), new PdfRectF(10, 30, 80, 40)]));
        Assert.Equal(AnnotKind.Underline, a.Kind);
        Assert.Equal(2, a.Quads.Length);
        Assert.Equal(new PdfRectF(10, 10, 80, 40), a.Rect);
        CheckCommon(a, ToolKind.Underline);
    }

    [Fact]
    public void Markup_NoTextRects_CreatesNothing()
        => Assert.Null(Make().CreateMarkup(ToolKind.HighlightText, Style, Page, []));

    [Fact]
    public void Pencil_Stroke_SimplifiedInkWithComputedRect()
    {
        var c = Make();
        c.Begin(ToolKind.Pencil, Style, Page, new(0, 0), 1f);
        c.Move(new(5, 0));
        c.Move(new(10, 0));
        var a = Added(c.End(new(10, 10)));
        Assert.Equal(AnnotKind.Ink, a.Kind);
        var stroke = Assert.Single(a.InkStrokes);
        Assert.Equal([new PdfPointF(0, 0), new PdfPointF(10, 0), new PdfPointF(10, 10)], stroke.ToArray());
        Assert.Equal(AnnotationGeometry.ComputeRect(a), a.Rect);
        Assert.Equal(2f, a.BorderWidth);
        CheckCommon(a, ToolKind.Pencil);
    }

    [Fact]
    public void Pencil_SinglePoint_CreatesDot()
    {
        var c = Make();
        c.Begin(ToolKind.Pencil, Style, Page, new(30, 40), 1f);
        var a = Added(c.End(new(30, 40)));
        var stroke = Assert.Single(a.InkStrokes);
        Assert.Single(stroke);
        Assert.Equal(new PdfPointF(30, 40), stroke[0]);
    }

    [Fact]
    public void StickyNote_Click_TextAnnotationTopLeftAtPoint()
    {
        var c = Make();
        c.Begin(ToolKind.StickyNote, Style, Page, new(10, 10), 1f);
        var a = Added(c.End(new(100, 200)));
        Assert.Equal(AnnotKind.Text, a.Kind);
        Assert.Equal(new PdfRectF(100, 180, 120, 200), a.Rect);
        Assert.Equal("", a.Contents);
        Assert.Same(a, c.LastCreatedSticky);
        CheckCommon(a, ToolKind.StickyNote);
    }

    [Fact]
    public void Cancel_ClearsPreview_NoCommand()
    {
        var c = Make();
        c.Begin(ToolKind.Rectangle, Style, Page, new(0, 0), 1f);
        c.Move(new(50, 50));
        Assert.NotNull(c.Preview);
        c.Cancel();
        Assert.Null(c.Preview);
        Assert.Null(c.End(new(60, 60)));
    }
}
