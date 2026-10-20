using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using PdfEditor.TestFixtures;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Objects;

/// <summary>M4 Task 9: 이미지·도형 넣기, 편집 탭 단추 연결, 객체 속성(합성 입력 — 실제 마우스·파일 대화상자는 검증 필요).</summary>
[Collection("TabSettings")]
public class AddToolsTests : ObjectUiTestBase
{
    private Task<DocumentSession> OpenDoc() => OpenAsync(p => ContentPdfFactory.CreateDocumentLike(p));

    private static byte[] Png() => TestImages.Png(60, 30, (x, y) => ((byte)(x * 4), (byte)(y * 8), 120, 255));

    private static MainWindowViewModel VmWith(DocumentTabViewModel tab)
    {
        var vm = new MainWindowViewModel();
        vm.Documents.Add(tab);
        vm.ActiveDocument = tab;
        vm.SelectedRibbonTab = "Edit";
        return vm;
    }

    // ---- 편집 탭 ----

    [Fact]
    public async Task EditTab_ButtonsEnabledWithDocument_ChooseTools()
    {
        var s = await OpenDoc();
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        var vm = VmWith(tab);
        (string Id, ToolKind Tool)[] cases =
        [
            ("edit.text", ToolKind.EditText), ("edit.object", ToolKind.EditObject), ("edit.addText", ToolKind.AddText),
            ("edit.shapeRect", ToolKind.AddShape), ("edit.shapeEllipse", ToolKind.AddEllipse), ("edit.shapeLine", ToolKind.AddLine),
        ];
        foreach (var (id, tool) in cases)
        {
            var cmd = vm.Commands.Find(id)!.Command;
            Assert.True(cmd.CanExecute(null), id);
            cmd.Execute(null);
            Assert.Equal(tool, tab.Tool);
        }
        Assert.True(vm.Commands.Find("edit.addImage")!.Command.CanExecute(null));
        // 켜진 도구 단추가 체크 표시된다
        var line = vm.CurrentRibbonGroups.SelectMany(g => g.Items).Single(b => b.Command.Id == "edit.shapeLine");
        Assert.True(line.IsChecked);
    }

    [Fact]
    public async Task EditTab_RestrictedDoc_Disabled()
    {
        var s = await OpenAsync(p => TestPdfFactory.CreateOwnerRestricted(p, allowPrint: true, allowModify: false, allowCopy: true));
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        var vm = VmWith(tab);
        Assert.False(tab.CanEditContent);
        foreach (var id in new[] { "edit.text", "edit.object", "edit.addText", "edit.addImage", "edit.shapeRect", "edit.shapeEllipse", "edit.shapeLine" })
        {
            var cmd = vm.Commands.Find(id)!.Command;
            Assert.False(cmd.CanExecute(null), id);
            cmd.Execute(null);
            Assert.Equal(ToolKind.Hand, tab.Tool);
        }
        foreach (var b in vm.CurrentRibbonGroups.SelectMany(g => g.Items).Where(b => b.Tool is { } t && t is ToolKind.EditText or ToolKind.EditObject or ToolKind.AddText or ToolKind.AddShape))
            Assert.Equal(Strings.Msg_EditRestricted, b.Tip);
    }

    [Fact]
    public async Task HomeTab_EditGroup_UsesRealCommands()
    {
        var vm = new MainWindowViewModel();
        var items = vm.Ribbon["Home"].Single(g => g.Name == Strings.Group_EditTools).Items;
        Assert.Equal(["edit.text", "edit.object", "edit.addText", "edit.addImage"], items.Select(i => i.CommandId));
        Assert.DoesNotContain(items, i => i.DisabledTip == Strings.Msg_ComingInM4M5);
        await Task.CompletedTask;
    }

    // ---- 이미지 ----

    [Fact]
    public async Task AddImage_PlacedCenteredSelected()
    {
        var s = await OpenDoc();
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        await tab.AddImageAsync(Png());
        var page = s.Pages[0].Id;
        var edit = Assert.IsType<InsertImageEdit>(Assert.Single(s.Content.For(page)));
        Assert.Equal(ToolKind.EditObject, tab.Tool);
        var sel = tab.Objects.Selected!;
        Assert.Equal(PdfPageObjectType.Image, sel.Type);
        Assert.Equal(ObjectRef.Inserted(edit.Id), sel.Ref);
        // 쪽(595×842) 가운데, 폭 50 % 이내, 비율 2:1
        Assert.Equal(595 / 2f, (sel.Bounds.Left + sel.Bounds.Right) / 2, 0.5f);
        Assert.Equal(842 / 2f, (sel.Bounds.Bottom + sel.Bounds.Top) / 2, 0.5f);
        Assert.True(sel.Bounds.Width <= 595 * 0.5f + 0.5f);
        Assert.Equal(2f, sel.Bounds.Width / sel.Bounds.Height, 0.01f);
        await s.Commands.UndoAsync();
        Assert.Empty(s.Content.For(page));
    }

    [Fact]
    public async Task AddImage_ViaCommand_UsesPicker()
    {
        var s = await OpenDoc();
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        var vm = VmWith(tab);
        vm.PickImageBytes = () => Task.FromResult<byte[]?>(Png());
        vm.Commands.Find("edit.addImage")!.Command.Execute(null);
        for (int i = 0; i < 200 && s.Content.For(s.Pages[0].Id).Length == 0; i++) await Task.Delay(25);
        Assert.IsType<InsertImageEdit>(Assert.Single(s.Content.For(s.Pages[0].Id)));
        // 취소(null)면 아무 일도 없다
        vm.PickImageBytes = () => Task.FromResult<byte[]?>(null);
        vm.Commands.Find("edit.addImage")!.Command.Execute(null);
        await Task.Delay(100);
        Assert.Single(s.Content.For(s.Pages[0].Id));
    }

    [Fact]
    public async Task AddImage_NotAnImage_NoticeAndNothingChanged()
    {
        var s = await OpenDoc();
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        string? notice = null;
        tab.NoticeRequested += m => notice = m;
        await tab.AddImageAsync([1, 2, 3, 4]);
        Assert.Equal(Strings.Msg_ImageInsertFailed, notice);
        Assert.Empty(s.Content.For(s.Pages[0].Id));
    }

    [Fact]
    public void ImagePlacement_RotatedPage_StaysCenteredAndUpright()
    {
        var page = new PdfRectF(0, 0, 595, 842);
        var m = ObjectToolController.ImagePlacement(page, 1, 100, 50);
        var b = ObjectToolController.BoundsOf(m);
        Assert.Equal(297.5f, (b.Left + b.Right) / 2, 0.5f);
        Assert.Equal(421f, (b.Bottom + b.Top) / 2, 0.5f);
        // 90도 돌아간 쪽: 보이는 폭은 842 → 이미지 폭 421, 쪽 좌표에서는 세로로 길다
        Assert.True(b.Height > b.Width);
        Assert.Equal(421f, b.Height, 0.5f);
    }

    [Fact]
    public void TextPlacement_RotatedPage_TextRunsAlongPageAxis()
    {
        var m = ObjectToolController.TextPlacement(new PdfPointF(100, 200), 12, 1);
        // 90도(반시계): 글자 진행 방향 (1,0) → 쪽 좌표 (0,1)
        var origin = m.Apply(new PdfPointF(0, 0));
        var adv = m.Apply(new PdfPointF(10, 0));
        Assert.Equal(0f, adv.X - origin.X, 0.001f);
        Assert.Equal(10f, adv.Y - origin.Y, 0.001f);
    }

    // ---- 도형 ----

    [Fact]
    public async Task AddShape_TinyDrag_CreatesNothing()
    {
        var h = Show(await OpenDoc());
        SetTool(h, ToolKind.AddShape);
        Drag(h, AtPt(h, 300, 500), AtPt(h, 301, 501));
        await Task.Delay(300);
        PumpUi(() => true);
        Assert.Empty(Edits(h));
        Assert.Null(ObjectToolController.MakeShape(ShapeKind.Line, new PdfPointF(0, 0), new PdfPointF(1, 1)));
        Assert.NotNull(ObjectToolController.MakeShape(ShapeKind.Line, new PdfPointF(0, 0), new PdfPointF(30, 0)));
        Assert.Null(ObjectToolController.MakeShape(ShapeKind.Rectangle, new PdfPointF(0, 0), new PdfPointF(50, 1)));
    }

    [Fact]
    public async Task AddShape_Ellipse_Undo()
    {
        var h = Show(await OpenDoc());
        SetTool(h, ToolKind.AddEllipse);
        Drag(h, AtPt(h, 300, 500), AtPt(h, 400, 560));
        PumpUi(() => Edits(h).Count == 1);
        var sh = Assert.IsType<InsertShapeEdit>(Edits(h)[0]);
        Assert.Equal(ShapeKind.Ellipse, sh.Shape.Kind);
        Assert.Equal(100f, sh.Shape.Bounds.Width, 1f);
        Assert.Equal(60f, sh.Shape.Bounds.Height, 1f);
        Assert.Equal(842f - 560f, sh.Shape.Bounds.Bottom, 1f);   // 화면 아래쪽 = PDF y가 작은 쪽
        await h.Session.Commands.UndoAsync();
        Assert.Empty(Edits(h));
    }

    [Fact]
    public async Task AddShape_RectAndLine_Created()
    {
        var h = Show(await OpenDoc());
        SetTool(h, ToolKind.AddShape);
        Drag(h, AtPt(h, 300, 500), AtPt(h, 380, 540));
        PumpUi(() => Edits(h).Count == 1);
        SetTool(h, ToolKind.AddLine);
        Drag(h, AtPt(h, 100, 600), AtPt(h, 300, 650));
        PumpUi(() => Edits(h).Count == 2);
        Assert.Equal(ShapeKind.Rectangle, ((InsertShapeEdit)Edits(h)[0]).Shape.Kind);
        var line = (InsertShapeEdit)Edits(h)[1];
        Assert.Equal(ShapeKind.Line, line.Shape.Kind);
        Assert.Equal(200f, line.Shape.B.X - line.Shape.A.X, 1f);
    }

    // ---- 객체 속성 ----

    private async Task<(DocumentTabViewModel Tab, DocumentSession S)> TabWithShapeAsync()
    {
        var s = await OpenDoc();
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        var spec = ObjectToolController.MakeShape(ShapeKind.Rectangle, new PdfPointF(100, 100), new PdfPointF(200, 160))!;
        var edit = new InsertShapeEdit(s.Content.NewId(), spec);
        await tab.RunContentCommandAsync(new ContentEditCommand(s.Pages[0].Id, [edit]));
        tab.Objects.Select(new SelectedObject(s.Pages[0].Id, ObjectRef.Inserted(edit.Id), PdfPageObjectType.Path, spec.Bounds));
        return (tab, s);
    }

    [Fact]
    public async Task Properties_ShapeShowsGeometryAndStyle_ChangeIsOneCommand()
    {
        var (tab, s) = await TabWithShapeAsync();
        var p = tab.ObjectProps;
        Assert.True(p.HasSelection);
        Assert.True(p.IsShape);
        Assert.Equal(100 * ObjectPropertiesViewModel_PtToMm, p.XMm, 0.05);
        Assert.Equal(100 * ObjectPropertiesViewModel_PtToMm, p.WidthMm, 0.05);
        Assert.Equal(1.5f, p.StrokeWidth);
        Assert.Null(p.Fill);

        var blue = new PdfEditor.Document.Annotations.PdfColor(0, 0, 255);
        await p.SetShapeStyleAsync(blue, 3f, new PdfEditor.Document.Annotations.PdfColor(255, 255, 0));
        var edits = s.Content.For(s.Pages[0].Id);
        var ss = Assert.IsType<SetShapeStyleEdit>(edits[^1]);
        Assert.Equal(blue, ss.Style.Stroke);
        Assert.Equal(3f, ss.Style.Width);
        Assert.Equal(2, edits.Length);
        await s.Commands.UndoAsync();   // 스타일 한 번 = 실행 취소 한 번
        Assert.Single(s.Content.For(s.Pages[0].Id));
    }

    private const double ObjectPropertiesViewModel_PtToMm = 25.4 / 72.0;

    [Fact]
    public async Task Properties_SetBounds_MovesAndResizes()
    {
        var (tab, s) = await TabWithShapeAsync();
        var p = tab.ObjectProps;
        double mm = ObjectPropertiesViewModel_PtToMm;
        // x 100→150pt, 너비 100→200pt
        await p.SetBoundsAsync(150 * mm, 100 * mm, 200 * mm, 60 * mm);
        var t = Assert.IsType<TransformEdit>(s.Content.For(s.Pages[0].Id)[^1]);
        Assert.Equal(2f, t.Then.A, 0.001f);
        Assert.Equal(1f, t.Then.D, 0.001f);
        Assert.Equal(150f - 2f * 100f, t.Then.E, 0.05f);
    }

    [Fact]
    public async Task Properties_AppText_SizeChange_ReplacesLine()
    {
        var s = await OpenDoc();
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        var page = s.Pages[0].Id;
        var r = await ObjectToolController.AddTextAsync(tab, page, new PdfPointF(100, 300), 0, "속성 ABC");
        Assert.NotNull(r.Created);
        tab.Objects.Select(new SelectedObject(page, r.Created!.Value, PdfPageObjectType.Path, r.CreatedBounds!.Value));
        var p = tab.ObjectProps;
        Assert.True(p.IsAppText);
        Assert.Equal(12f, p.TextSizePt);
        await p.SetTextStyleAsync(24f, null);
        var edits = s.Content.For(page);
        var lines = edits.OfType<InsertAppTextEdit>().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(24f, lines[1].Line.SizePt);
        Assert.Equal("속성 ABC", lines[1].Line.Text);
        Assert.Contains(edits, e => e is DeactivateEdit d && d.Target == r.Created);
        Assert.Equal(ObjectRef.Inserted(lines[1].Id), tab.Objects.Selected!.Ref);   // 새 줄이 선택을 이어받는다
        await s.Commands.UndoAsync();
        Assert.Single(s.Content.For(page));
    }
}

public class ObjectPropertiesPanelTests : ObjectUiTestBase
{
    [Fact]
    public async Task Panel_ShowsSelectedShapeValues()
    {
        var s = await OpenAsync(p => ContentPdfFactory.CreateDocumentLike(p));
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        var spec = ObjectToolController.MakeShape(ShapeKind.Rectangle, new PdfPointF(100, 100), new PdfPointF(200, 160))!;
        var edit = new InsertShapeEdit(s.Content.NewId(), spec);
        await tab.RunContentCommandAsync(new ContentEditCommand(s.Pages[0].Id, [edit]));
        var (boxCount, w) = HeadlessUi.Run(() =>
        {
            tab.Objects.Select(new SelectedObject(s.Pages[0].Id, ObjectRef.Inserted(edit.Id), PdfPageObjectType.Path, spec.Bounds));
            var panel = new ObjectPropertiesPanel { DataContext = tab.ObjectProps };
            var win = new Avalonia.Controls.Window { Width = 260, Height = 600, Content = panel };
            win.Show();
            win.UpdateLayout();
            var boxes = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(panel).OfType<Avalonia.Controls.NumericUpDown>().ToList();
            return (boxes.Count, boxes.First().Value);
        });
        Assert.True(boxCount >= 5);
        Assert.NotNull(w);
    }
}
