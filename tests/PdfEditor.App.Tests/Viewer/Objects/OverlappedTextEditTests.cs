using Avalonia.Input;
using PdfEditor.App.Documents;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.TestFixtures;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Objects;

/// <summary>가짜 굵게(같은 글을 어긋나게 여러 번 겹쳐 찍음) 줄: 누르면 글이 채워진 편집 칸이 열리고, 확정하면 겹친 객체도 함께 꺼진다.</summary>
[Collection("TabSettings")]
public class OverlappedTextEditTests : ObjectUiTestBase
{
    private async Task<Host> OpenOnFauxBold()
    {
        var s = await OpenAsync(p => ContentPdfFactory.CreateFauxBoldLine(p));
        var h = Show(s);
        SetTool(h, ToolKind.EditText);
        Click(h, AtPt(h, 80, 96));
        PumpUi(() => EditorOpen(h) || Ui(() => h.View.IsParagraphEditorOpen));
        return h;
    }

    [Fact]
    public async Task EditText_OnOverlappedLine_ShowsOriginalText()
    {
        var h = await OpenOnFauxBold();
        string shown = Ui(() => h.View.IsParagraphEditorOpen ? h.View.ParagraphEditorForTest!.Text : h.View.LineEditorForTest!.Text);
        Assert.Equal(ContentPdfFactory.LineFauxBold, shown.Trim());
    }

    [Fact]
    public async Task EditText_OnOverlappedLine_Commit_DeactivatesAllOverlappedObjects()
    {
        var h = await OpenOnFauxBold();
        Assert.True(EditorOpen(h), "한 줄 편집 창이 열려야 한다");
        HeadlessUi.Run(() => { h.View.LineEditorForTest!.SetText("NEW HEADER"); return 0; });
        EditorKey(h, Key.Enter);
        PumpUi(() => Edits(h).Count > 0);
        Assert.Equal(4, Edits(h).OfType<DeactivateEdit>().Count());
    }
}
