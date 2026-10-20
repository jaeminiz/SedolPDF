using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.TestFixtures;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Objects;

/// <summary>M4 Task 8: 텍스트 편집(한 줄 편집 창)·텍스트 추가 화면 시험(합성 입력 — 실제 마우스·한글 IME는 검증 필요).</summary>
[Collection("TabSettings")]
public class TextEditInputTests : ObjectUiTestBase
{
    private Task<PdfEditor.Document.DocumentSession> OpenDoc() => OpenAsync(p => ContentPdfFactory.CreateDocumentLike(p));

    private static void SetEditorText(Host h, string text) => HeadlessUi.Run(() => { h.View.LineEditorForTest!.SetText(text); return 0; });

    /// <summary>Arial 줄(기준선 100pt 바로 위)을 눌러 편집 창을 연다.</summary>
    private Host OpenEditorOnArialLine(PdfEditor.Document.DocumentSession s)
    {
        var h = Show(s);
        SetTool(h, ToolKind.EditText);
        Click(h, AtPt(h, 80, 96));
        WaitEditorOpen(h);
        return h;
    }

    [Fact]
    public async Task EditText_EnterCommits_EscCancels()
    {
        var h = OpenEditorOnArialLine(await OpenDoc());
        Assert.Equal(ContentPdfFactory.LineArial, Ui(() => h.View.LineEditorForTest!.Text));   // 원래 글이 채워진다
        // Esc: 취소 — 아무것도 바뀌지 않는다
        SetEditorText(h, "Arial 321 CBA");
        EditorKey(h, Key.Escape);
        PumpUi(() => !EditorOpen(h));
        Assert.Empty(Edits(h));
        // 다시 열어 Enter: 확정 — 원래 글꼴로 바꾼다(새 줄 넣기 + 원래 줄 끄기, 실행 취소 한 번)
        Click(h, AtPt(h, 80, 96));
        WaitEditorOpen(h);
        SetEditorText(h, "Arial 321 CBA");
        EditorKey(h, Key.Enter);
        PumpUi(() => Edits(h).Count > 0);
        Assert.Contains(Edits(h), e => e is InsertTextLikeEdit t && t.Text == "Arial 321 CBA");
        Assert.Contains(Edits(h), e => e is DeactivateEdit);
        Assert.Null(Toast(h));   // 원래 글꼴이라 알림 없음
        await h.Session.Commands.UndoAsync();
        Assert.Empty(Edits(h));
    }

    [Fact]
    public async Task EditText_UnchangedText_NoEdit()
    {
        var h = OpenEditorOnArialLine(await OpenDoc());
        EditorKey(h, Key.Enter);
        PumpUi(() => !EditorOpen(h));
        await Task.Delay(100);
        PumpUi(() => true);
        Assert.Empty(Edits(h));
    }

    [Fact]
    public async Task EditText_ImeComposition_EnterIgnored()
    {
        var h = OpenEditorOnArialLine(await OpenDoc());
        SetEditorText(h, "Arial 321 CBA");
        HeadlessUi.Run(() =>
        {
            var box = h.View.LineEditorForTest!.FindControl<TextBox>("Box")!;
            box.GetVisualDescendants().OfType<TextPresenter>().First().PreeditText = "ㅎ";   // 한글 조합 중인 글자
            return 0;
        });
        EditorKey(h, Key.Enter);   // 조합을 끝내는 키: 확정하지 않는다
        EditorKey(h, Key.Escape);  // 조합을 취소하는 키: 편집 창을 닫지 않는다
        Assert.True(EditorOpen(h));
        Assert.Empty(Edits(h));
        HeadlessUi.Run(() =>
        {
            var box = h.View.LineEditorForTest!.FindControl<TextBox>("Box")!;
            box.GetVisualDescendants().OfType<TextPresenter>().First().PreeditText = null;
            return 0;
        });
        EditorKey(h, Key.Enter);   // 조합이 끝난 뒤의 Enter가 확정
        PumpUi(() => Edits(h).Count > 0);
    }

    [Fact]
    public async Task EditText_FallbackNoticeShown()
    {
        var h = OpenEditorOnArialLine(await OpenDoc());
        SetEditorText(h, "Arial 가나다 ABC");   // 서브셋 글꼴에 없는 글자
        EditorKey(h, Key.Enter);
        PumpUi(() => Edits(h).Any(e => e is InsertAppTextEdit));
        PumpUi(() => Toast(h) is not null);
        Assert.Equal(Strings.Msg_TextFallbackApp, Toast(h));
    }

    [Fact]
    public async Task EditText_NoTextObjects_NoticeShown()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateBlank(p, 1, 595, 842));
        var h = Show(s);
        var notices = new List<string>();
        h.Tab.NoticeRequested += notices.Add;
        SetTool(h, ToolKind.EditText);
        Click(h, AtPt(h, 100, 100));
        PumpUi(() => notices.Count > 0);
        Assert.Equal(Strings.Msg_NoEditableText, notices[0]);
    }

    [Fact]
    public async Task EditText_ClickOnImage_NoEditorNoNotice()
    {
        var h = Show(await OpenDoc());
        var notices = new List<string>();
        h.Tab.NoticeRequested += notices.Add;
        SetTool(h, ToolKind.EditText);
        Click(h, AtPt(h, 120, 245));   // 이미지 위: 쪽에 글자는 있으므로 안내하지 않는다
        await Task.Delay(300);
        PumpUi(() => true);
        Assert.False(EditorOpen(h));
        Assert.Empty(notices);
    }

    [Fact]
    public async Task EditText_FormObject_NoticeSaysGrouped()
    {
        var s = await OpenAsync(ContentPdfFactory.CreateDrawingLike);
        var h = Show(s);
        var notices = new List<string>();
        h.Tab.NoticeRequested += notices.Add;
        SetTool(h, ToolKind.EditText);
        // 도면형: 쪽의 거의 모든 내용이 폼 안에 있다. 표제란(왼쪽 위 근처)을 눌러 본다.
        bool got = false;
        foreach (var (x, y) in new[] { (60.0, 60.0), (100.0, 100.0), (200.0, 150.0), (300.0, 200.0), (500.0, 300.0) })
        {
            Click(h, AtPt(h, x, y));
            await Task.Delay(200);
            PumpUi(() => true);
            if (notices.Contains(Strings.Msg_FormChildNotice)) { got = true; break; }
        }
        Assert.True(got, string.Join(" | ", notices));
        Assert.False(EditorOpen(h));
    }

    [Fact]
    public async Task AddText_CreatesAppText_Undo()
    {
        var h = Show(await OpenDoc());
        SetTool(h, ToolKind.AddText);
        Click(h, AtPt(h, 200, 600));
        WaitEditorOpen(h);
        SetEditorText(h, "새 텍스트 ABC 123");
        EditorKey(h, Key.Enter);
        PumpUi(() => Edits(h).Count == 1);
        var line = Assert.IsType<InsertAppTextEdit>(Edits(h)[0]);
        Assert.Equal("새 텍스트 ABC 123", line.Line.Text);
        Assert.Equal(12f, line.Line.SizePt);
        // 클릭한 점이 글자 상자의 왼쪽 위(PDF y = 842 - 600)
        Assert.Equal(200f, line.Line.Bounds.Left, 1f);
        Assert.Equal(842f - 600f, line.Line.Bounds.Top, 6f);
        await h.Session.Commands.UndoAsync();
        Assert.Empty(Edits(h));
    }

    [Fact]
    public async Task AddText_EmptyText_CreatesNothing()
    {
        var h = Show(await OpenDoc());
        SetTool(h, ToolKind.AddText);
        Click(h, AtPt(h, 200, 600));
        WaitEditorOpen(h);
        EditorKey(h, Key.Enter);
        PumpUi(() => !EditorOpen(h));
        await Task.Delay(200);
        PumpUi(() => true);
        Assert.Empty(Edits(h));
    }

    [Fact]
    public async Task AddText_DisabledOnEncrypted_WithTooltip()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateEncrypted(p, 1, 400, 600, "secret"), "secret");
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        var vm = new MainWindowViewModel();
        vm.Documents.Add(tab);
        vm.ActiveDocument = tab;
        vm.SelectedRibbonTab = "Edit";
        var add = vm.Commands.Find("edit.addText")!.Command;
        Assert.False(add.CanExecute(null));
        add.Execute(null);
        Assert.Equal(ToolKind.Hand, tab.Tool);   // 직접 실행해도 도구가 바뀌지 않는다
        var btn = vm.CurrentRibbonGroups.SelectMany(g => g.Items).Single(b => b.Command.Id == "edit.addText");
        Assert.Equal(Strings.Msg_AddTextDisabledEncrypted, btn.Tip);
        // 텍스트 편집·개체 편집·도형은 암호 문서에서도 쓸 수 있다
        Assert.True(vm.Commands.Find("edit.text")!.Command.CanExecute(null));
        Assert.True(vm.Commands.Find("edit.object")!.Command.CanExecute(null));
        Assert.True(vm.Commands.Find("edit.shapeRect")!.Command.CanExecute(null));
    }
}
