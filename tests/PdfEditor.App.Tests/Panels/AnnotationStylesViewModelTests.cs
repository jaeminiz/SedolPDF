using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

[Collection("TabSettings")]
public class AnnotationStylesViewModelTests : PanelTestBase, IDisposable
{
    private static readonly PdfColor Red = PdfColor.FromHex("#E5372D");
    private static readonly PdfColor Blue = PdfColor.FromHex("#005FB8");
    private static readonly PdfColor Green = PdfColor.FromHex("#1F9D55");
    private static readonly DateTimeOffset Fixed = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
    private readonly SettingsStore _store;

    public AnnotationStylesViewModelTests()
    {
        Directory.CreateDirectory(Dir);
        _store = new SettingsStore(Path.Combine(Dir, "settings.json"));
        DocumentTabViewModel.Settings = _store;
    }

    void IDisposable.Dispose() => DocumentTabViewModel.Settings = null;

    private async Task<(DocumentTabViewModel Tab, AnnotationStylesViewModel Vm, PageId Page, List<Annotation> Items)> SetupAsync(
        params AnnotKind[] kinds)
    {
        string p = Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".pdf");
        TestPdfFactory.CreateLabeled(p, [(400, 600)]);
        var tab = await OpenTab(p);
        var page = tab.Session!.Pages[0].Id;
        var items = new List<Annotation>();
        int i = 0;
        foreach (var kind in kinds)
        {
            var a = new Annotation
            {
                Id = tab.Session.Annotations.NewId(), Kind = kind, Rect = new PdfRectF(10 + i * 70, 10, 70 + i * 70, 60),
                Color = Red, BorderWidth = 1f, Opacity = 1f, Author = "A", Flags = PdfAnnotFlags.Print,
                CreatedAt = Fixed.AddDays(-1), ModifiedAt = Fixed.AddDays(-1),
                FontSizePt = 12f,
            };
            await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Add(page, a));
            items.Add(a);
            i++;
        }
        tab.Tool = ToolKind.AnnotSelect;
        var vm = new AnnotationStylesViewModel(tab, null, () => Fixed);
        return (tab, vm, page, items);
    }

    private static Annotation Current(DocumentTabViewModel tab, PageId page, AnnotId id) =>
        tab.Session!.Annotations.Get(page).Items.Single(v => v.Data.Id == id).Data;

    private static readonly AnnotationStyle Full = new("Full", null, "#005FB8", "#1F9D55", 0.5f, 3f, 20f, "OpenArrow", "ClosedArrow", 1.5f);

    [Fact]
    public async Task DefaultStylesWhenNone()
    {
        var (_, vm, _, _) = await SetupAsync();
        Assert.Null(_store.Load().Styles);
        Assert.Equal([Strings.Style_ReviewRed, Strings.Style_HighlightYellow, Strings.Style_ApprovedGreen], vm.Available.Select(s => s.Name));
        Assert.Equal(nameof(AnnotKind.Highlight), vm.All.Single(s => s.Name == Strings.Style_HighlightYellow).AppliesTo);
    }

    [Fact]
    public async Task Available_FilteredBySelectionAndTool()
    {
        var (tab, vm, page, items) = await SetupAsync(AnnotKind.Square, AnnotKind.Highlight);
        tab.Selection.Set(page, [items[0].Id]);   // 사각형: 모두에게 맞는 2개만
        Assert.Equal([Strings.Style_ReviewRed, Strings.Style_ApprovedGreen], vm.Available.Select(s => s.Name));
        tab.Selection.Set(page, [items[1].Id]);   // 강조: 3개 모두
        Assert.Equal(3, vm.Available.Count);
        tab.Selection.Set(page, [items[0].Id, items[1].Id]);   // 섞인 선택: 하나라도 맞으면
        Assert.Equal(3, vm.Available.Count);
        tab.Tool = ToolKind.Pencil;
        Assert.Equal(2, vm.Available.Count);
        tab.Tool = ToolKind.HighlightText;
        Assert.Equal(3, vm.Available.Count);
    }

    [Fact]
    public async Task Apply_ToSelection_OneCommand_KindAware_UndoRestores()
    {
        var (tab, vm, page, items) = await SetupAsync(AnnotKind.Square, AnnotKind.Highlight, AnnotKind.Line, AnnotKind.FreeText);
        tab.Selection.Set(page, items.Select(a => a.Id).ToList());
        await vm.ApplyAsync(Full);

        var sq = Current(tab, page, items[0].Id);
        Assert.Equal(Blue, sq.Color); Assert.Equal(0.5f, sq.Opacity); Assert.Equal(3f, sq.BorderWidth);
        Assert.Equal(Green, sq.FillColor); Assert.Equal(1.5f, sq.CloudIntensity);
        Assert.Equal(LineEnding.None, sq.StartEnding); Assert.Equal(12f, sq.FontSizePt);   // 종류에 안 맞는 속성은 그대로

        var hl = Current(tab, page, items[1].Id);   // 강조: 색·불투명도만
        Assert.Equal(Blue, hl.Color); Assert.Equal(0.5f, hl.Opacity);
        Assert.Equal(1f, hl.BorderWidth); Assert.Null(hl.FillColor); Assert.Equal(0f, hl.CloudIntensity);

        var ln = Current(tab, page, items[2].Id);   // 선: 두께·선 끝, 클라우드·채우기 없음
        Assert.Equal(3f, ln.BorderWidth); Assert.Equal(LineEnding.OpenArrow, ln.StartEnding); Assert.Equal(LineEnding.ClosedArrow, ln.EndEnding);
        Assert.Null(ln.FillColor); Assert.Equal(0f, ln.CloudIntensity); Assert.Equal(12f, ln.FontSizePt);

        var ft = Current(tab, page, items[3].Id);   // 텍스트 상자: 글자 크기, 선 끝·클라우드 없음
        Assert.Equal(20f, ft.FontSizePt); Assert.Equal(LineEnding.None, ft.StartEnding); Assert.Equal(0f, ft.CloudIntensity);
        Assert.All(items, a => Assert.Equal(Fixed, Current(tab, page, a.Id).ModifiedAt));

        await tab.Session!.Commands.UndoAsync();   // 명령 하나로 전부 되돌아간다
        foreach (var a in items)
        {
            var back = Current(tab, page, a.Id);
            Assert.Equal(Red, back.Color); Assert.Equal(1f, back.Opacity); Assert.Equal(1f, back.BorderWidth);
            Assert.Equal(12f, back.FontSizePt); Assert.Equal(LineEnding.None, back.StartEnding); Assert.Equal(0f, back.CloudIntensity);
        }
    }

    [Fact]
    public async Task Apply_StyleWithAppliesTo_SkipsOtherKinds()
    {
        var (tab, vm, page, items) = await SetupAsync(AnnotKind.Square, AnnotKind.Highlight);
        tab.Selection.Set(page, [items[0].Id, items[1].Id]);
        await vm.ApplyAsync(vm.All.Single(s => s.AppliesTo == nameof(AnnotKind.Highlight)));
        Assert.Equal(Red, Current(tab, page, items[0].Id).Color);   // 사각형은 그대로
        Assert.Equal(PdfColor.FromHex("#FFD60A"), Current(tab, page, items[1].Id).Color);
        Assert.Equal(0.5f, Current(tab, page, items[1].Id).Opacity);
    }

    [Fact]
    public async Task Apply_WithoutFillStyle_KeepsExistingFill()
    {
        var (tab, vm, page, items) = await SetupAsync(AnnotKind.Square);
        tab.Selection.Set(page, [items[0].Id]);
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Replace(page, [(items[0], items[0] with { FillColor = Green })]));
        await vm.ApplyAsync(vm.All[0]);   // 검토 빨강: 채우기 없음 = 건드리지 않음
        Assert.Equal(Green, Current(tab, page, items[0].Id).FillColor);
    }

    [Fact]
    public async Task Apply_ToTool_UpdatesToolDefaults()
    {
        var (tab, vm, _, _) = await SetupAsync();
        tab.Tool = ToolKind.Rectangle;
        await vm.ApplyAsync(Full);
        var st = DocumentTabViewModel.StyleFor(ToolKind.Rectangle);
        Assert.Equal(Blue, st.Color); Assert.Equal(Green, st.Fill); Assert.Equal(3f, st.Width); Assert.Equal(0.5f, st.Opacity, 3);
        Assert.Equal(ToolDefaults_For(ToolKind.Pencil), DocumentTabViewModel.StyleFor(ToolKind.Pencil));   // 다른 도구는 그대로
        Assert.False(tab.Session!.Commands.CanUndo);   // 문서는 바뀌지 않는다
        // 적용 종류가 다른 스타일은 도구에 반영하지 않는다
        tab.Tool = ToolKind.Pencil;
        await vm.ApplyAsync(vm.All.Single(s => s.AppliesTo == nameof(AnnotKind.Highlight)));
        Assert.Equal(ToolDefaults_For(ToolKind.Pencil), DocumentTabViewModel.StyleFor(ToolKind.Pencil));
    }

    private static PdfEditor.App.Viewer.Annotations.ToolStyle ToolDefaults_For(ToolKind t) =>
        PdfEditor.App.Viewer.Annotations.ToolDefaults.For(t);

    [Fact]
    public async Task Apply_WithoutAnnotatePermissionOrTarget_DoesNothing()
    {
        var (tab, vm, page, items) = await SetupAsync(AnnotKind.Square);
        await vm.ApplyAsync(Full);   // 선택도 도구도 없음
        Assert.Equal(Red, Current(tab, page, items[0].Id).Color);
    }

    [Fact]
    public async Task Apply_BrokenStyle_NoticeAndNoChange()
    {
        var (tab, vm, page, items) = await SetupAsync(AnnotKind.Square);
        tab.Selection.Set(page, [items[0].Id]);
        string? notice = null;
        tab.NoticeRequested += m => notice = m;
        await vm.ApplyAsync(new AnnotationStyle("bad", null, "not-a-colour", null, 1f, 1f));
        Assert.Equal(Strings.Msg_StyleBroken, notice);
        Assert.Equal(Red, Current(tab, page, items[0].Id).Color);
    }

    [Fact]
    public async Task SaveCurrentAs_DuplicateNameRejected()
    {
        var (tab, vm, page, items) = await SetupAsync(AnnotKind.Square);
        tab.Selection.Set(page, [items[0].Id]);
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Replace(page, [(items[0], items[0] with { Color = Blue, BorderWidth = 4f, FillColor = Green, Opacity = 0.7f })]));
        string? notice = null;
        tab.NoticeRequested += m => notice = m;

        Assert.True(vm.SaveCurrentAs("  mine "));
        Assert.False(vm.SaveCurrentAs("mine"));
        Assert.False(vm.SaveCurrentAs("MINE"));   // 대소문자 무시
        Assert.False(vm.SaveCurrentAs(Strings.Style_ReviewRed));   // 기본 스타일 이름과도 겹치지 않는다
        Assert.False(vm.SaveCurrentAs("   "));
        Assert.Equal(Strings.Msg_StyleNameInvalid, notice);

        var saved = _store.Load().Styles!;
        Assert.Equal(4, saved.Count);   // 기본 3개 + 새 1개 (한 번 저장하면 전체 목록)
        var mine = saved.Single(s => s.Name == "mine");
        Assert.Equal(nameof(AnnotKind.Square), mine.AppliesTo);
        Assert.Equal("#005FB8", mine.ColorHex); Assert.Equal("#1F9D55", mine.FillHex);
        Assert.Equal(4f, mine.Width); Assert.Equal(0.7f, mine.Opacity, 3);
        Assert.Null(mine.FontSizePt); Assert.Null(mine.StartEnding);
    }

    [Fact]
    public async Task SaveCurrentAs_FromTool_UsesToolStyle()
    {
        var (tab, vm, _, _) = await SetupAsync();
        DocumentTabViewModel.SaveToolStyle(ToolKind.Rectangle, new(Blue, Green, 0.8f, 2.5f));
        Assert.False(vm.SaveCurrentAs("x"));   // 대상 없음
        tab.Tool = ToolKind.Rectangle;
        Assert.True(vm.SaveCurrentAs("x"));
        var s = _store.Load().Styles!.Single(t => t.Name == "x");
        Assert.Equal(nameof(AnnotKind.Square), s.AppliesTo); Assert.Equal("#005FB8", s.ColorHex);
        Assert.Equal("#1F9D55", s.FillHex); Assert.Equal(2.5f, s.Width);
        Assert.Contains(s, vm.Available);
    }

    [Fact]
    public async Task SaveCurrentAs_FromLine_KeepsEndings_AndAppliesToLines()
    {
        var (tab, vm, page, items) = await SetupAsync(AnnotKind.Line, AnnotKind.Line);
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Replace(page,
            [(items[0], items[0] with { EndEnding = LineEnding.ClosedArrow, BorderWidth = 3f })]));
        tab.Selection.Set(page, [items[0].Id]);
        Assert.True(vm.SaveCurrentAs("arrow"));
        tab.Selection.Set(page, [items[1].Id]);
        await vm.ApplyAsync(vm.All.Single(s => s.Name == "arrow"));
        var l = Current(tab, page, items[1].Id);
        Assert.Equal(LineEnding.ClosedArrow, l.EndEnding); Assert.Equal(3f, l.BorderWidth);
    }

    [Fact]
    public async Task DeleteAndRename_Persist_DuplicateRenameRejected()
    {
        var (tab, vm, _, _) = await SetupAsync();
        string? notice = null;
        tab.NoticeRequested += m => notice = m;
        Assert.True(vm.Delete(Strings.Style_ApprovedGreen));   // 기본 스타일도 지울 수 있다
        Assert.Equal(2, _store.Load().Styles!.Count);
        Assert.Equal(2, vm.Available.Count);
        Assert.False(vm.Delete("nothing"));
        Assert.False(vm.Rename(Strings.Style_ReviewRed, Strings.Style_HighlightYellow));
        Assert.Equal(Strings.Msg_StyleNameInvalid, notice);
        Assert.False(vm.Rename(Strings.Style_ReviewRed, " "));
        Assert.True(vm.Rename(Strings.Style_ReviewRed, "renamed"));
        Assert.Equal(["renamed", Strings.Style_HighlightYellow], _store.Load().Styles!.Select(s => s.Name));
        Assert.True(vm.Delete("renamed")); Assert.True(vm.Delete(Strings.Style_HighlightYellow));
        Assert.Empty(_store.Load().Styles!);   // 모두 지우면 빈 목록 그대로 (기본값으로 되돌아가지 않음)
        Assert.Empty(vm.Available);
    }

    [Fact]
    public async Task TargetText_ToolSelectionAndMixed()
    {
        var (tab, vm, page, items) = await SetupAsync(AnnotKind.Square, AnnotKind.Square, AnnotKind.Circle);
        Assert.False(vm.HasTarget); Assert.Equal("", vm.TargetText);
        tab.Tool = ToolKind.Cloud;
        Assert.True(vm.HasTarget);
        Assert.Equal(string.Format(Strings.Format_TargetTool, Strings.AnnotKind_Cloud), vm.TargetText);
        tab.Tool = ToolKind.AnnotSelect;
        tab.Selection.Set(page, [items[0].Id, items[1].Id]);
        Assert.Equal(string.Format(Strings.Format_TargetSelection, Strings.AnnotKind_Square, 2), vm.TargetText);
        tab.Selection.Set(page, [items[0].Id, items[2].Id]);
        Assert.Equal(string.Format(Strings.Format_TargetMixed, 2), vm.TargetText);
    }

    [Fact]
    public async Task EncryptedDoc_PatchPropertiesNotApplied_OthersAre()
    {
        string p = Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".pdf");
        AnnotatedPdfFactory.CreatePatchKinds(p, "secret");
        var tab = await OpenTab(p, "secret");
        await tab.Session!.AnnotationsReady;
        Assert.False(tab.CanUsePatchFeatures);
        var page = tab.Session.Pages[0].Id;
        var sq = new Annotation { Id = tab.Session.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(10, 10, 80, 60), Color = Red, BorderWidth = 1f, Opacity = 1f, Flags = PdfAnnotFlags.Print };
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Add(page, sq));
        tab.Tool = ToolKind.AnnotSelect;
        tab.Selection.Set(page, [sq.Id]);
        var vm = new AnnotationStylesViewModel(tab);
        Assert.True(vm.CanEdit); Assert.False(vm.CanEditPatchProperties);
        await vm.ApplyAsync(Full);
        var now = Current(tab, page, sq.Id);
        Assert.Equal(Blue, now.Color); Assert.Equal(3f, now.BorderWidth);
        Assert.Equal(0f, now.CloudIntensity);   // 클라우드는 패치가 필요해 적용하지 않는다
    }
}
