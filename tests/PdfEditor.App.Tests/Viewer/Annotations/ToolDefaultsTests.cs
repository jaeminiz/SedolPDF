using PdfEditor.App.Documents;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Annotations;

public class ToolDefaultsTests
{
    [Theory]
    [InlineData(ToolKind.TextBox)]
    [InlineData(ToolKind.Callout)]
    public void New_text_boxes_default_to_white_fill(ToolKind tool)
        => Assert.Equal(PdfColor.FromHex("#FFFFFF"), ToolDefaults.For(tool).Fill);

    [Theory]
    [InlineData(ToolKind.Typewriter)]
    [InlineData(ToolKind.Rectangle)]
    [InlineData(ToolKind.Ellipse)]
    [InlineData(ToolKind.Polygon)]
    public void Other_tools_keep_no_fill(ToolKind tool) => Assert.Null(ToolDefaults.For(tool).Fill);
}
