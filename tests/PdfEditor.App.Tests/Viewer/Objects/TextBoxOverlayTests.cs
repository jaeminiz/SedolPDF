using PdfEditor.App.Viewer.Objects;
using PdfEditor.TestFixtures;
using Pdf.Native;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Objects;

[Collection("TabSettings")]
public class TextBoxOverlayTests : ObjectUiTestBase
{
    private async Task<IReadOnlyList<PdfRectF>> BoxesOf(Action<string> create)
    {
        var s = await OpenAsync(create);
        var id = s.Pages[0].Id;
        return await s.Thread.InvokeAsync(() => TextBoxOverlay.Boxes(s.GetObjectIndex(id), new HashSet<long>()));
    }

    [Fact]
    public async Task Boxes_DocumentLike_OnlyTextLines()
        => Assert.Equal(3, (await BoxesOf(p => ContentPdfFactory.CreateDocumentLike(p))).Count);   // Arial·한글·Helvetica (이미지·사각형·선 제외)

    [Fact]
    public async Task Boxes_FauxBold_MergedIntoOne()
        => Assert.Single(await BoxesOf(p => ContentPdfFactory.CreateFauxBoldLine(p)));

    [Fact]
    public void HoverBox_PicksSmallestCoveringBox()
    {
        var big = new PdfRectF(0, 0, 100, 50);
        var small = new PdfRectF(10, 10, 30, 20);
        Assert.Equal(small, TextBoxOverlay.HoverBox([big, small], new PdfPointF(15, 15), 0.5f));
        Assert.Null(TextBoxOverlay.HoverBox([big, small], new PdfPointF(200, 200), 0.5f));
    }
}
