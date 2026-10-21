using System.Collections.Immutable;
using System.Text;
using Pdf.Native;
using PdfEditor.Appearance.Patching;
using PdfEditor.ContentStream;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Content;

namespace PdfEditor.Appearance.Tests.Patching;

public class ContentRewritePlannerTests
{
    private static ShapeSpec Rect(ShapeStyle? st = null) =>
        new(ShapeKind.Rectangle, new PdfRectF(10, 20, 110, 70), default, default, st ?? ShapeStyle.Default);

    private static AppTextSpec Line() =>
        new("A", 12, new PdfColor(0, 0, 0), PdfMatrix.Identity, new PdfRectF(0, 0, 10, 10), [], "BT /{F} 12 Tf 10 10 Td <0001> Tj ET\n", new FontUse([], []));

    private static string Parse(NewSnippet s)
    {
        var bytes = Encoding.Latin1.GetBytes(s.Content);
        var pc = ContentLexer.Parse(bytes);
        Assert.True(pc.Ok, pc.Failure);
        return string.Join(' ', pc.Ops.Select(o => o.Name));
    }

    [Fact]
    public void Disable_And_Transform_OnOriginals_FoldPerObject()
    {
        var plan = ContentRewritePlanner.Plan([
            new TransformEdit(1, ObjectRef.Original(3), PdfMatrix.Translate(5, 0)),
            new TransformEdit(2, ObjectRef.Original(3), PdfMatrix.Translate(0, 7)),
            new TransformEdit(3, ObjectRef.Original(4), PdfMatrix.Translate(1, 1)),
            new DeactivateEdit(4, ObjectRef.Original(4)),
        ]);
        Assert.True(plan.Ok);
        Assert.Equal(2, plan.ObjectEdits.Count);
        var t = Assert.IsType<TransformObject>(plan.ObjectEdits[0]);
        Assert.Equal(3, t.ObjectIndex);
        Assert.Equal(new Mat(1, 0, 0, 1, 5, 7), t.Then);
        Assert.IsType<DisableObject>(plan.ObjectEdits[1]);   // 옮긴 뒤 지우면 지우기만
    }

    [Fact]
    public void Shape_Serializes_To_ParsableOperators_WithStyle()
    {
        var plan = ContentRewritePlanner.Plan([
            new InsertShapeEdit(1, Rect(new ShapeStyle(new PdfColor(255, 0, 0), 2, new PdfColor(0, 0, 255)))),
            new InsertShapeEdit(2, new ShapeSpec(ShapeKind.Ellipse, new PdfRectF(0, 0, 40, 20), default, default, ShapeStyle.Default)),
            new InsertShapeEdit(3, new ShapeSpec(ShapeKind.Line, default, new PdfPointF(1, 2), new PdfPointF(30, 40), ShapeStyle.Default)),
        ]);
        Assert.True(plan.Ok);
        Assert.Equal(3, plan.Snippets.Count);
        Assert.Equal("RG w rg m l l l h B", Parse(plan.Snippets[0]));
        Assert.Contains("1 0 0 RG 2 w", plan.Snippets[0].Content);
        Assert.EndsWith("B\n", plan.Snippets[0].Content);
        Assert.Equal("RG w m c c c c h S", Parse(plan.Snippets[1]));
        Assert.Equal("RG w m l S", Parse(plan.Snippets[2]));
    }

    [Fact]
    public void Shape_Style_Move_Delete_FoldIntoTheNewObject()
    {
        var id = 1L;
        var plan = ContentRewritePlanner.Plan([
            new InsertShapeEdit(id, Rect()),
            new SetShapeStyleEdit(2, ObjectRef.Inserted(id), new ShapeStyle(null, 1, new PdfColor(0, 255, 0))),
            new TransformEdit(3, ObjectRef.Inserted(id), PdfMatrix.Translate(4, 5)),
            new InsertShapeEdit(9, Rect()),
            new DeactivateEdit(10, ObjectRef.Inserted(9)),
        ]);
        Assert.True(plan.Ok);
        var s = Assert.Single(plan.Snippets);
        Assert.StartsWith("1 0 0 1 4 5 cm\n", s.Content);
        Assert.Equal("cm rg m l l l h f", Parse(s));
    }

    [Fact]
    public void Image_Placement_And_Move_AreOneMatrix_AppText_IsLast()
    {
        var plan = ContentRewritePlanner.Plan([
            new InsertAppTextEdit(1, Line()),
            new InsertImageEdit(2, "img1", new PdfMatrix(100, 0, 0, 50, 10, 10)),
            new TransformEdit(3, ObjectRef.Inserted(2), PdfMatrix.Translate(5, 5)),
            new TransformEdit(4, ObjectRef.Inserted(1), PdfMatrix.Translate(2, 3)),
        ]);
        Assert.True(plan.Ok);
        Assert.Equal(2, plan.Snippets.Count);
        Assert.Equal("img1", plan.Snippets[0].ImageAssetId);
        Assert.Equal("100 0 0 50 15 15 cm\n/{I} Do\n", plan.Snippets[0].Content);
        Assert.NotNull(plan.Snippets[1].Font);
        Assert.StartsWith("1 0 0 1 2 3 cm\nBT", plan.Snippets[1].Content);
    }

    [Fact]
    public void Unsupported_Edits_Fail()
    {
        Assert.False(ContentRewritePlanner.Plan([new InsertTextLikeEdit(1, ObjectRef.Original(0), "x")]).Ok);
        Assert.False(ContentRewritePlanner.Plan([new SetShapeStyleEdit(1, ObjectRef.Original(0), ShapeStyle.Default)]).Ok);
        Assert.False(ContentRewritePlanner.Plan([new DeactivateEdit(1, ObjectRef.Inserted(77))]).Ok);
    }
}
