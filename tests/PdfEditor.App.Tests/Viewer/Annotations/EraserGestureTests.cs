using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Annotations;

public class EraserGestureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly PageId Page = new(7);
    private static readonly ToolStyle Style = new(PdfColor.FromHex("#E5372D"), null, 1f, 2f);

    private static Annotation Ink(long id, params (float, float)[] pts) => new()
    {
        Id = new AnnotId(id),
        Kind = AnnotKind.Ink,
        BorderWidth = 2f,
        InkStrokes = [[.. pts.Select(p => new PdfPointF(p.Item1, p.Item2))]],
    };

    private static AnnotationToolController Make(params Annotation[] onPage)
        => new(() => new AnnotId(99), () => Now, () => "me", t => "s") { PageAnnotationSource = _ => onPage };

    private static AnnotationChange[] Changes(ChangeAnnotationsCommand cmd)
        => (AnnotationChange[])typeof(ChangeAnnotationsCommand)
            .GetField("_changes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cmd)!;

    [Fact]
    public void EraseEverything_DeletesAnnotation()
    {
        var ink = Ink(1, (0, 0), (20, 0));
        var c = Make(ink);
        c.Begin(ToolKind.Eraser, Style, Page, new(0, 0), 1f);
        c.Move(new(10, 0));
        Assert.True(c.Previews!.ContainsKey(ink.Id));
        var cmd = c.End(new(20, 0));
        var ch = Assert.Single(Changes(cmd!));
        Assert.Equal(ink, ch.Before);
        Assert.Null(ch.After);
        Assert.Null(c.Previews);
    }

    [Fact]
    public void ErasePart_ReplacesWithRemainingStrokes_OneCommand()
    {
        var a = Ink(1, (0, 0), (100, 0));
        var b = Ink(2, (0, 50), (100, 50));
        var c = Make(a, b);
        c.Begin(ToolKind.Eraser, Style, Page, new(50, 0), 1f);
        var cmd = c.End(new(50, 0));
        var ch = Assert.Single(Changes(cmd!));
        Assert.Equal(a, ch.Before);
        Assert.Equal(2, ch.After!.InkStrokes.Length);
        Assert.Equal(Now, ch.After.ModifiedAt);
        Assert.Equal(AnnotationGeometry.ComputeRect(ch.After), ch.After.Rect);
    }

    [Fact]
    public void EraserMissesAll_NoCommand()
    {
        var c = Make(Ink(1, (0, 0), (100, 0)));
        c.Begin(ToolKind.Eraser, Style, Page, new(50, 80), 1f);
        c.Move(new(60, 80));
        Assert.Null(c.Previews);
        Assert.Null(c.End(new(70, 80)));
    }

    [Fact]
    public void Eraser_IgnoresNonInkAnnotations()
    {
        var sq = new Annotation { Id = new AnnotId(3), Kind = AnnotKind.Square, Rect = new PdfRectF(0, 0, 100, 100) };
        var c = Make(sq);
        c.Begin(ToolKind.Eraser, Style, Page, new(50, 50), 1f);
        Assert.Null(c.End(new(50, 50)));
    }
}
