using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;
using PdfEditor.TextEdit.Paragraphs;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Objects;

/// <summary>
/// M5 Task 8: 문단 편집 화면 시험(헤드리스 합성 입력 — 실제 마우스·한글 IME·다른 뷰어는 검증 필요).
/// 샘플은 ParagraphPdfFactory의 doc-en-left(영문 4문단, 15pt 간격, 기준선 y=90부터)와 dwg-table-norules(선 없는 표).
/// </summary>
[Collection("TabSettings")]
public class ParagraphEditInputTests : ObjectUiTestBase
{
    private static readonly Lazy<IReadOnlyList<ParagraphSample>> Samples = new(() =>
        ParagraphPdfFactory.CreateAll(Path.Combine(Path.GetTempPath(), "pdfeditor-m5ui-samples-" + Guid.NewGuid().ToString("N"))));

    private Task<DocumentSession> OpenSample(string name) =>
        OpenAsync(p => File.Copy(Samples.Value.Single(s => s.Name == name).Path, p));

    /// <summary>첫 문단 첫 줄(기준선 y=90) 위를 눌러 문단 편집 창을 연다.</summary>
    private async Task<Host> OpenParagraph(string sample = "doc-en-left", double x = 100, double y = 86)
    {
        var h = Show(await OpenSample(sample));
        SetTool(h, ToolKind.EditText);
        Click(h, AtPt(h, x, y));
        PumpUi(() => ParaOpen(h));
        WaitPreview(h);
        return h;
    }

    private static bool ParaOpen(Host h) => Ui(() => h.View.IsParagraphEditorOpen);
    private static void WaitPreview(Host h) => PumpUi(() => Ui(() => h.View.ParagraphPreviewIdleForTest && h.View.ParagraphPreviewForTest.LineCount > 0));
    private static ParagraphPreview Preview(Host h) => Ui(() => h.View.ParagraphPreviewForTest);
    private static void WaitPlanned(Host h, Func<ParagraphPreview, bool> ok)
    {
        try { PumpUi(() => Ui(() => h.View.ParagraphPreviewIdleForTest) && ok(Preview(h)), 6000); }
        catch (Exception) { throw new Xunit.Sdk.XunitException("preview: " + Preview(h) + " | " + Ui(() => Editor(h).StatusText)); }
    }

    private static ParagraphEditorPopup Editor(Host h) => Ui(() => h.View.ParagraphEditorForTest!);
    private static string EditorText(Host h) => Ui(() => Editor(h).Text);
    private static void SetText(Host h, string t) => HeadlessUi.Run(() => { Editor(h).SetText(t); return 0; });
    private static IReadOnlyList<ReplaceParagraphEdit> ParaEdits(Host h) => Edits(h).OfType<ReplaceParagraphEdit>().ToList();

    private static void Press(Host h, Key k, KeyModifiers m = KeyModifiers.None) => HeadlessUi.Run(() =>
    {
        var box = Editor(h).FindControl<TextBox>("Box")!;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = k, KeyModifiers = m });
        return 0;
    });

    private static void SetPreedit(Host h, string? text) => HeadlessUi.Run(() =>
    {
        Editor(h).FindControl<TextBox>("Box")!.GetVisualDescendants().OfType<TextPresenter>().First().PreeditText = text;
        return 0;
    });

    // ---- 열기 ----

    [Fact]
    public async Task ClickLine_HighConfidence_ShowsBoxAndEditor()
    {
        var h = await OpenParagraph();
        var ps = Ui(() => h.View.ParagraphSessionForTest)!;
        Assert.True(ps.Model.Lines.Length >= 3);
        Assert.False(EditorOpen(h));                                   // 한 줄 창이 아니다
        Assert.Equal(ps.InitialText, EditorText(h));                    // 문단 글이 채워진다
        Assert.StartsWith(ps.Model.Lines[0].Text.Split(' ')[0], EditorText(h));
        Assert.Equal(ParagraphBoxState.Normal, Preview(h).State);
        Assert.Contains(string.Format(Strings.Para_Lines, ps.Model.Lines.Length, "").Split('/')[0], Ui(() => Editor(h).StatusText));
        Assert.Empty(Edits(h));
    }

    [Fact]
    public async Task ClickTableCell_ShowsSingleLineEditor_WithReason()
    {
        var h = Show(await OpenSample("dwg-table-norules"));
        SetTool(h, ToolKind.EditText);
        Click(h, AtPt(h, 215, 135));   // "SEA WATER" 칸
        WaitEditorOpen(h);
        Assert.False(ParaOpen(h));
        PumpUi(() => Toast(h) is not null);
        Assert.Equal(Strings.Para_ReasonTable, Toast(h));
    }

    [Fact]
    public async Task OneLineOnlyToggle_UsesM4Editor()
    {
        var h = Show(await OpenSample("doc-en-left"));
        HeadlessUi.Run(() => { h.Tab.OneLineOnly = true; return 0; });
        SetTool(h, ToolKind.EditText);
        Click(h, AtPt(h, 100, 86));
        WaitEditorOpen(h);
        Assert.False(ParaOpen(h));
        Assert.Null(Toast(h));   // 설정이 켜져 있으면 조용히 한 줄 편집
    }

    [Fact]
    public async Task EncryptedDocument_ParagraphToolFallsBackToSingleLine()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateEncrypted(p, 1, 400, 600, "secret"), "secret");
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        Assert.False(tab.CanUseAppFontText);
        var pick = new PdfEditor.Document.Content.PageObjectEntry(ObjectRef.Original(0), PdfPageObjectType.Text, 0, new PdfRectF(0, 0, 10, 10), default, "x", false);
        var idx = new PdfEditor.Document.Content.PageObjectIndex(s.Pages[0].Id, 0, [pick]);
        var start = await ParagraphController.StartAsync(tab, s.Pages[0].Id, pick, idx);
        Assert.Null(start.Session);
        Assert.Equal(Strings.Para_Encrypted, start.Reason);
    }

    [Fact]
    public async Task ClickOnFormObject_Message()
    {
        var s = await OpenAsync(ContentPdfFactory.CreateDrawingLike);
        var h = Show(s);
        var notices = new List<string>();
        h.Tab.NoticeRequested += notices.Add;
        SetTool(h, ToolKind.EditText);
        bool got = false;
        foreach (var (x, y) in new[] { (60.0, 60.0), (100.0, 100.0), (200.0, 150.0), (300.0, 200.0), (500.0, 300.0) })
        {
            Click(h, AtPt(h, x, y));
            await Task.Delay(200);
            PumpUi(() => true);
            if (notices.Contains(Strings.Msg_FormChildNotice)) { got = true; break; }
        }
        Assert.True(got);
        Assert.False(ParaOpen(h));
    }

    // ---- 키 ----

    [Fact]
    public async Task Ime_Composing_EnterEscIgnored()
    {
        var h = await OpenParagraph();
        SetText(h, EditorText(h) + " 추가");
        SetPreedit(h, "ㅎ");
        Press(h, Avalonia.Input.Key.Enter, KeyModifiers.Control);   // 조합을 끝내는 키: 확정하지 않는다
        Press(h, Avalonia.Input.Key.Escape);                         // 조합만 취소: 편집 창을 닫지 않는다
        Assert.True(ParaOpen(h));
        Assert.Empty(Edits(h));
        SetPreedit(h, null);
        HeadlessUi.Run(() => { Editor(h).ImeGuard.ForgetRecentEnd(); return 0; });   // 시간이 충분히 흘렀다
        Press(h, Avalonia.Input.Key.Enter, KeyModifiers.Control);   // 조합이 끝난 뒤의 Ctrl+Enter가 확정
        PumpUi(() => ParaEdits(h).Count == 1);
    }

    [Fact]
    public async Task CtrlEnter_Commits_EnterInsertsNewline()
    {
        var h = await OpenParagraph();
        string text = EditorText(h);
        SetText(h, "Commissioning Report " + text);
        Press(h, Avalonia.Input.Key.Enter);   // 그냥 Enter는 줄바꿈 입력이지 확정이 아니다
        Assert.True(ParaOpen(h));
        Assert.Empty(Edits(h));
        Press(h, Avalonia.Input.Key.Enter, KeyModifiers.Control);
        PumpUi(() => ParaEdits(h).Count == 1);
        PumpUi(() => !ParaOpen(h));
        var e = ParaEdits(h)[0];
        Assert.StartsWith("Commissioning Report", e.Meta.NewText);
        Assert.False(e.Remove.IsEmpty);
        Assert.True(e.OriginalFontLines.Length + e.AppLines.Length > 0);
    }

    [Fact]
    public async Task Esc_Cancels_NoEdit()
    {
        var h = await OpenParagraph();
        SetText(h, "아무 글");
        Press(h, Avalonia.Input.Key.Escape);
        PumpUi(() => !ParaOpen(h));
        await Task.Delay(100);
        PumpUi(() => true);
        Assert.Empty(Edits(h));
    }

    [Fact]
    public async Task Unchanged_Close_NoEdit()
    {
        var h = await OpenParagraph();
        Press(h, Avalonia.Input.Key.Enter, KeyModifiers.Control);
        PumpUi(() => !ParaOpen(h));
        await Task.Delay(100);
        PumpUi(() => true);
        Assert.Empty(Edits(h));
        Assert.False(h.Session.IsDirty);
    }

    [Fact]
    public async Task OutsideClick_Commits()
    {
        var h = await OpenParagraph();
        SetText(h, EditorText(h).Insert(0, "Testing "));
        PumpUi(() => true);
        Click(h, AtPt(h, 500, 700));   // 창 밖
        PumpUi(() => ParaEdits(h).Count == 1);
        PumpUi(() => !ParaOpen(h));
    }

    // ---- 넘침·겹침 ----

    [Fact]
    public async Task PageBottomOverflow_CommitBlocked_ShowsCount()
    {
        var h = await OpenParagraph();
        SetText(h, string.Join(" ", Enumerable.Repeat("The commissioning of the system shall be witnessed by the owner.", 140)));
        WaitPlanned(h, p => p.Overflow);
        var p = Preview(h);
        Assert.Equal(ParagraphBoxState.Blocked, p.State);
        Assert.True(p.LineCount > p.MaxLines);
        Assert.Contains(p.MaxLines.ToString(), Ui(() => Editor(h).StatusText));
        Assert.False(Ui(() => Editor(h).CommitEnabled));
        Press(h, Avalonia.Input.Key.Enter, KeyModifiers.Control);   // 확정 시도: 막힌다
        await Task.Delay(300);
        PumpUi(() => true);
        Assert.True(ParaOpen(h));
        Assert.Empty(Edits(h));
        Assert.Equal(ParagraphBoxState.Blocked, Preview(h).State);
    }

    [Fact]
    public async Task OverlapOtherObjects_CommitAllowed_BoxTurnsOrange()
    {
        var h = await OpenParagraph();
        // 첫 문단을 길게 늘려 아래 문단과 겹치게 한다(쪽 아래까지는 넘치지 않는다)
        SetText(h, string.Join(" ", Enumerable.Repeat("The commissioning of the system shall be witnessed by the owner.", 14)));
        WaitPlanned(h, p => p.Overlap);
        var p = Preview(h);
        Assert.False(p.Overflow);
        Assert.Equal(ParagraphBoxState.Overlap, p.State);
        Assert.True(Ui(() => Editor(h).CommitEnabled));
        Press(h, Avalonia.Input.Key.Enter, KeyModifiers.Control);
        PumpUi(() => ParaEdits(h).Count == 1);
    }

    // ---- 상자 ----

    [Fact]
    public async Task DragBoxEdge_RewrapsOnRelease()
    {
        var h = await OpenParagraph();
        int before = Preview(h).LineCount;
        double w0 = Ui(() => Editor(h).BoxWidth);
        HeadlessUi.Run(() => { Editor(h).SimulateWidthDrag(-150 * K, leading: false); return 0; });   // 오른쪽 가장자리를 150pt 안으로
        WaitPlanned(h, p => p.LineCount > before);
        Assert.True(Ui(() => Editor(h).BoxWidth) < w0);
        Assert.True(Ui(() => h.View.ParagraphSessionForTest!.WidthChanged));
        // 글을 안 고쳤어도 폭이 바뀌었으면 확정할 수 있다 — 한 번의 실행 취소로 돌아온다
        Press(h, Avalonia.Input.Key.Enter, KeyModifiers.Control);
        PumpUi(() => ParaEdits(h).Count == 1);
        var e = ParaEdits(h)[0];
        Assert.True(e.OriginalFontLines.Length + e.AppLines.Length > before);
        await h.Session.Commands.UndoAsync();
        Assert.Empty(ParaEdits(h));
    }

    /// <summary>폭 손잡이가 마우스로 집을 수 있는가(템플릿 없는 Thumb은 모양도 클릭 영역도 없었다)(2026-10-27 실제 화면 시험에서 손잡이를 끌 수 없었음).</summary>
    [Fact]
    public async Task WidthGrips_HaveVisualAndAreHitTestable()
    {
        var h = await OpenParagraph();
        var hit = Ui(() =>
        {
            var pop = Editor(h);
            pop.UpdateLayout();
            var r = pop.FindControl<Avalonia.Controls.Border>("RightGrip")!;
            var pt = Avalonia.VisualExtensions.TranslatePoint(r, new Avalonia.Point(r.Bounds.Width / 2, r.Bounds.Height / 2), pop) ?? default;
            var top = pop.InputHitTest(pt) as Avalonia.Visual;
            bool onGrip = top is not null && (ReferenceEquals(top, r) || top.GetVisualAncestors().Contains(r));
            return onGrip;
        });
        Assert.True(hit, "손잡이 가운데를 마우스로 누르면 손잡이가 받아야 한다");
    }

    /// <summary>진짜 마우스 입력(눌러서 끌고 놓기)으로 오른쪽 폭 손잡이를 100 DIP 끌면 폭이 그만큼만 줄어야 한다(2026-10-27 실제 화면에서 폭이 7.8 mm로 무너졌음).</summary>
    [Fact]
    public async Task WidthGrip_RealMouseDrag_ShrinksByDraggedAmount()
    {
        var h = await OpenParagraph();
        double w0 = Ui(() => h.View.ParagraphSessionForTest!.WidthPt);
        double scale = Ui(() => Editor(h).EditorFontSize) / Ui(() => h.View.ParagraphSessionForTest!.SizePt);
        var start = Ui(() =>
        {
            var pop = Editor(h);
            pop.UpdateLayout();
            var r = pop.FindControl<Avalonia.Controls.Border>("RightGrip")!;
            return Avalonia.VisualExtensions.TranslatePoint(r, new Avalonia.Point(r.Bounds.Width / 2, r.Bounds.Height / 2), h.Window) ?? default;
        });
        HeadlessUi.Run(() =>
        {
            h.Window.MouseDown(start, MouseButton.Left);
            h.Window.MouseMove(start + new Avalonia.Point(-30, 0));
            h.Window.MouseMove(start + new Avalonia.Point(-100, 0));
            h.Window.MouseUp(start + new Avalonia.Point(-100, 0), MouseButton.Left);
            return 0;
        });
        PumpUi(() => Ui(() => h.View.ParagraphSessionForTest!.WidthPt) < w0 - 1);
        double w1 = Ui(() => h.View.ParagraphSessionForTest!.WidthPt);
        Assert.InRange(w0 - w1, 100 / scale * 0.8, 100 / scale * 1.2);
    }

    [Fact]
    public async Task PanelWidth_ChangesBox()
    {
        var h = await OpenParagraph();
        Assert.True(Ui(() => h.Tab.ObjectProps.HasParagraph));
        int before = Preview(h).LineCount;
        double mm = Ui(() => h.Tab.ObjectProps.ParagraphWidthMm);
        await Ui(() => h.Tab.ObjectProps.SetParagraphWidthAsync(mm * 0.5));
        WaitPlanned(h, p => p.LineCount > before);
        Assert.Equal(mm * 0.5, Ui(() => h.Tab.ObjectProps.ParagraphWidthMm), 0.5);
        Press(h, Avalonia.Input.Key.Escape);
        PumpUi(() => !ParaOpen(h));
        Assert.False(Ui(() => h.Tab.ObjectProps.HasParagraph));
    }

    [Fact]
    public async Task ExtendBox_PlusMinus_ChangesLines()
    {
        var h = await OpenParagraph();
        int n = Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length);
        HeadlessUi.Run(() => { Editor(h).RequestExtend(0, 1); return 0; });   // 아래 줄 포함
        PumpUi(() => Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length) == n + 1);
        HeadlessUi.Run(() => { Editor(h).RequestExtend(0, -1); return 0; });  // 아래 줄 제외
        PumpUi(() => Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length) == n);
        // 글을 고친 뒤에는 줄을 더하고 뺄 수 없다(직접 고친 글이 사라지지 않게)
        SetText(h, EditorText(h) + " x");
        HeadlessUi.Run(() => { Editor(h).RequestExtend(0, 1); return 0; });
        Assert.Equal(n, Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length));
    }

    [Fact]
    public async Task EmptyText_DeletesParagraph_ConfirmFlow()
    {
        var h = await OpenParagraph();
        SetText(h, "");
        Press(h, Avalonia.Input.Key.Enter, KeyModifiers.Control);   // 확인 대화상자 없이 바로 지운다 — 실행 취소 가능
        PumpUi(() => ParaEdits(h).Count == 1);
        var e = ParaEdits(h)[0];
        Assert.False(e.Remove.IsEmpty);
        Assert.Equal(0, e.OriginalFontLines.Length + e.AppLines.Length);
        await h.Session.Commands.UndoAsync();
        Assert.Empty(ParaEdits(h));
    }

    [Fact]
    public async Task ZoomChange_KeepsBoxAligned()
    {
        var h = await OpenParagraph();
        double f0 = Ui(() => Editor(h).EditorFontSize);
        HeadlessUi.Run(() => { h.Tab.Scale = h.Tab.Scale * 2; return 0; });
        PumpUi(() => Ui(() => Editor(h).EditorFontSize) > f0 * 1.9);
        Assert.Equal(f0 * 2, Ui(() => Editor(h).EditorFontSize), 1.0);
        Assert.True(ParaOpen(h));
    }

    [Fact]
    public async Task RotatedPage_EditorOrientation()
    {
        var s = await OpenSample("doc-en-left");
        await s.Commands.ExecuteAsync(new RotatePagesCommand([s.Pages[0].Id], -1));
        var h = Show(s);
        SetTool(h, ToolKind.EditText);
        Click(h, AtPt(h, 86, 495));   // 반시계 방향 90° 회전: 쪽 좌표의 (x, 위에서의 y)가 화면 (위에서의 y, 595 − x)에 온다
        PumpUi(() => ParaOpen(h));
        WaitPreview(h);
        var ps = Ui(() => h.View.ParagraphSessionForTest)!;
        Assert.Equal(0, ps.Angle);                            // 배치는 쪽 좌표 기준
        Assert.True(Editor(h) is not null && Ui(() => Editor(h).EditorFontSize) > 6);   // 편집 칸은 화면에서 바로 선다(회전 없음)
        SetText(h, EditorText(h).Insert(0, "Testing "));
        Press(h, Avalonia.Input.Key.Enter, KeyModifiers.Control);
        PumpUi(() => ParaEdits(h).Count == 1);
    }

    // ---- 실행 취소·다시 편집 ----

    [Fact]
    public async Task UndoRedo_OneStepPerParagraph()
    {
        var h = await OpenParagraph();
        SetText(h, EditorText(h).Insert(0, "Testing "));
        Press(h, Avalonia.Input.Key.Enter, KeyModifiers.Control);
        PumpUi(() => ParaEdits(h).Count == 1);
        await h.Session.Commands.UndoAsync();
        Assert.Empty(ParaEdits(h));
        await h.Session.Commands.RedoAsync();
        Assert.Single(ParaEdits(h));
    }

    [Fact]
    public async Task ReEdit_OpensWithEditedText_NoStacking()
    {
        var h = await OpenParagraph();
        SetText(h, EditorText(h).Insert(0, "Testing "));
        Press(h, Avalonia.Input.Key.Enter, KeyModifiers.Control);
        PumpUi(() => ParaEdits(h).Count == 1);
        PumpUi(() => !ParaOpen(h));
        // 다시 같은 문단을 누르면 고친 글이 열린다
        Click(h, AtPt(h, 100, 86));
        PumpUi(() => ParaOpen(h));
        WaitPreview(h);
        Assert.Contains("Testing", EditorText(h));
        Assert.NotNull(Ui(() => h.View.ParagraphSessionForTest!.Previous));
        SetText(h, EditorText(h).Replace("Testing", "Retesting"));
        Press(h, Avalonia.Input.Key.Enter, KeyModifiers.Control);
        PumpUi(() => ParaEdits(h).Count == 1 && ParaEdits(h)[0].Meta.NewText.Contains("Retesting"));
        Assert.Single(ParaEdits(h));   // 쌓이지 않고 대체
    }

    // ---- 메인 창 ----

    [Fact]
    public async Task MainWindow_OneLineOnlyToggle_AppliesToDocuments()
    {
        var s = await OpenSample("doc-en-left");
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        var vm = new MainWindowViewModel();
        vm.Documents.Add(tab);
        vm.ActiveDocument = tab;
        vm.SelectedRibbonTab = "Edit";
        Assert.False(tab.OneLineOnly);
        var cmd = vm.Commands.Find("edit.oneLineOnly")!.Command;
        Assert.True(cmd.CanExecute(null));
        cmd.Execute(null);
        Assert.True(vm.OneLineOnly);
        Assert.True(tab.OneLineOnly);
        var btn = vm.CurrentRibbonGroups.SelectMany(g => g.Items).Single(b => b.Command.Id == "edit.oneLineOnly");
        Assert.True(btn.IsChecked);
        cmd.Execute(null);
        Assert.False(tab.OneLineOnly);
        Assert.False(btn.IsChecked);
    }
}
