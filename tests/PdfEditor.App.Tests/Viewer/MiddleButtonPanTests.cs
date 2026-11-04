using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using PdfEditor.App.Documents;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Tests.Viewer.Objects;
using PdfEditor.TestFixtures;
using Xunit;

namespace PdfEditor.App.Tests.Viewer;

/// <summary>마우스 휠 버튼을 누른 채 끌면 도구와 상관없이 화면이 옮겨지고(손), 놓으면 도구는 그대로다.</summary>
[Collection("TabSettings")]
public class MiddleButtonPanTests : ObjectUiTestBase
{
    [Fact]
    public async Task MiddleDrag_PansWhateverTheTool_AndKeepsTool()
    {
        var s = await OpenAsync(p => ContentPdfFactory.CreateDocumentLike(p, pages: 3));
        var h = Show(s);
        SetTool(h, ToolKind.EditText);
        var start = new Point(h.PageRect.X + 200, h.PageRect.Y + 400);
        double before = Ui(() => ((Avalonia.Controls.ScrollViewer)h.Window.Content!).Offset.Y);
        HeadlessUi.Run(() =>
        {
            h.Window.MouseMove(start);
            h.Window.MouseDown(start, MouseButton.Middle);
            for (int i = 1; i <= 5; i++) h.Window.MouseMove(new Point(start.X, start.Y - 40 * i));
            h.Window.MouseUp(new Point(start.X, start.Y - 200), MouseButton.Middle);
            return 0;
        });
        double after = Ui(() => ((Avalonia.Controls.ScrollViewer)h.Window.Content!).Offset.Y);
        Assert.True(after > before + 100, $"위로 끌면 화면이 아래로 내려간다 (before {before}, after {after})");
        Assert.Equal(ToolKind.EditText, Ui(() => h.Tab.Tool));
        Assert.Empty(Edits(h));   // 객체를 선택하거나 편집하지 않는다
    }
}
