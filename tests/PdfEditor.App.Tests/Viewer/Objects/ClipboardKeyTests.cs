using System.Reflection;
using Avalonia.Headless;
using Avalonia.Input;
using PdfEditor.App.Documents;
using PdfEditor.App.Viewer;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document.Content;
using PdfEditor.TestFixtures;
using PdfEditor.App.Tests.Viewer.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Objects;

/// <summary>0.9.0 Task 9: 단축키 연결(헤드리스 합성 입력 — 실제 키보드는 손 시험). 객체 편집 도구에서만 개체 Ctrl+C/V/D가 먹는다.</summary>
public class ClipboardKeyTests : ObjectUiTestBase
{
    private async Task<Host> ShowDocument()
    {
        ClipboardController.Shared.Clear();
        return Show(await OpenAsync(p => ContentPdfFactory.CreateDocumentLike(p)));
    }

    private static void Warm(Host h) => HeadlessUi.Run(() =>
    {
        h.View.InvalidateVisual();
        var st = typeof(DocumentView).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.View)!;
        var idx = (System.Collections.IDictionary)st.GetType().GetField("ObjIndex")!.GetValue(st)!;
        Pump(() => { h.Window.UpdateLayout(); _ = h.Window.CaptureRenderedFrame(); return idx.Count > 0; });
        return 0;
    });

    private static void CtrlKey(Host h, Key k) =>
        HeadlessUi.Run(() => { h.View.Focus(); h.Window.KeyPress(k, RawInputModifiers.Control, PhysicalKey.None, null); return 0; });

    [Fact]
    public async Task CtrlD_OnSelectedObject_Duplicates_WithoutTouchingClipboard()
    {
        var h = await ShowDocument();
        SetTool(h, ToolKind.EditObject);
        Warm(h);
        Click(h, AtPt(h, 120, 245));   // 이미지
        Assert.NotNull(h.Tab.Objects.Selected);
        CtrlKey(h, Key.D);
        PumpUi(() => Edits(h).OfType<DuplicateObjectEdit>().Count() == 1);
        var d = Edits(h).OfType<DuplicateObjectEdit>().Single();
        Assert.Equal(10f, d.Then.E, 0.01f);
        Assert.Equal(-10f, d.Then.F, 0.01f);
        Assert.Null(ClipboardController.Shared.Current);
    }

    [Fact]
    public async Task CtrlC_ThenCtrlV_PastesCopyOffset()
    {
        var h = await ShowDocument();
        SetTool(h, ToolKind.EditObject);
        Warm(h);
        Click(h, AtPt(h, 120, 245));
        CtrlKey(h, Key.C);
        PumpUi(() => ClipboardController.Shared.Current is not null);
        Assert.Empty(Edits(h));   // 복사는 문서를 바꾸지 않는다
        CtrlKey(h, Key.V);
        PumpUi(() => Edits(h).OfType<DuplicateObjectEdit>().Count() == 1);
        Assert.Equal(10f, Edits(h).OfType<DuplicateObjectEdit>().Single().Then.E, 0.01f);
    }

    [Fact]
    public async Task CtrlD_InTextEditTool_DoesNothing()
    {
        var h = await ShowDocument();
        SetTool(h, ToolKind.EditObject);
        Warm(h);
        Click(h, AtPt(h, 120, 245));
        SetTool(h, ToolKind.EditText);
        CtrlKey(h, Key.D);
        System.Threading.Thread.Sleep(300);
        Assert.Empty(Edits(h));
    }
}
