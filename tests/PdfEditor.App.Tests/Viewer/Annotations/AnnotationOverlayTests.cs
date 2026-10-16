using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Tests.Viewer.Annotations;

public class AnnotationOverlayTests
{
    private static Annotation Square(long id) => new()
    {
        Id = new AnnotId(id),
        Kind = AnnotKind.Square,
        Rect = new PdfRectF(20, 20, 80, 80),
        Color = new PdfColor(200, 0, 0),
        FillColor = new PdfColor(200, 0, 0),
        BorderWidth = 2,
        Flags = PdfAnnotFlags.Print,
    };

    private static int NonWhite(byte[] b)
    {
        int n = 0;
        for (int i = 0; i < b.Length; i += 4) if (b[i] < 245 || b[i + 1] < 245 || b[i + 2] < 245) n++;
        return n;
    }

    private static byte[] Draw(PageAnnotations page, IReadOnlyDictionary<AnnotId, Annotation?> previews)
    {
        var tr = new PageTransform(System.Drawing.RectangleF.FromLTRB(0, 0, 100, 100), 0, 1.0);
        return HeadlessUi.RenderBgra(100, 100, ctx =>
            AnnotationOverlay.DrawPage(ctx, page, new Avalonia.Rect(0, 0, 100, 100), tr, 0, previews));
    }

    private static readonly IReadOnlyDictionary<AnnotId, Annotation?> None = new Dictionary<AnnotId, Annotation?>();

    [Fact]
    public void Geometry_IsCachedPerInstanceAndRotation() => HeadlessUi.Run(() =>
    {
        var a = Square(1);
        Assert.Same(AppearanceGeometry.Get(a, 0), AppearanceGeometry.Get(a, 0));
        Assert.Same(AppearanceGeometry.Get(a, 1), AppearanceGeometry.Get(a, 1));
        Assert.NotSame(AppearanceGeometry.Get(a, 0), AppearanceGeometry.Get(a with { BorderWidth = 3 }, 0));
        Assert.Same(AppearanceGeometry.Get(a, 0), AppearanceGeometry.Get(a, 4));   // 회전은 0~3으로 정규화
        return 0;
    });

    [Fact]
    public void OnlyModelDrawnItemsAreDrawn()
    {
        var page = new PageAnnotations(new PageId(1), [new AnnotationView(Square(1), false)]);
        Assert.Equal(0, NonWhite(Draw(page, None)));
        page = new PageAnnotations(new PageId(1), [new AnnotationView(Square(1), true)]);
        Assert.True(NonWhite(Draw(page, None)) > 1000);
    }

    [Fact]
    public void Preview_ReplacesAndNullHidesAndNewIdIsAdded()
    {
        var a = Square(1);
        var page = new PageAnnotations(new PageId(1), [new AnnotationView(a, true)]);
        int plain = NonWhite(Draw(page, None));

        var hidden = new Dictionary<AnnotId, Annotation?> { [a.Id] = null };
        Assert.Equal(0, NonWhite(Draw(page, hidden)));

        var moved = new Dictionary<AnnotId, Annotation?> { [a.Id] = a with { Rect = new PdfRectF(10, 10, 30, 30) } };
        int smaller = NonWhite(Draw(page, moved));
        Assert.InRange(smaller, 1, plain - 1);

        var added = new Dictionary<AnnotId, Annotation?> { [new AnnotId(2)] = Square(2) with { Rect = new PdfRectF(0, 0, 10, 10) } };
        Assert.True(NonWhite(Draw(page, added)) > plain);
        // 쪽에 없는 번호의 미리보기는 주석이 없는 쪽에도 그려진다.
        Assert.True(NonWhite(Draw(PageAnnotations.Empty(new PageId(1)), added)) > 0);
    }
}
