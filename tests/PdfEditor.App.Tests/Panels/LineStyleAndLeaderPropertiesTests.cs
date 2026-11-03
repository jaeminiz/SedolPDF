using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Settings;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

/// <summary>0.4.0 Task 2·3: 속성 패널·형식 탭이 쓰는 모델의 선 모양·양쪽 화살표·지시선 추가/삭제.</summary>
[Collection("TabSettings")]
public class LineStyleAndLeaderPropertiesTests : PanelTestBase, IDisposable
{
    private static readonly DateTimeOffset Fixed = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    private readonly SettingsStore _store;

    public LineStyleAndLeaderPropertiesTests()
    {
        Directory.CreateDirectory(Dir);
        _store = new SettingsStore(Path.Combine(Dir, "settings.json"));
        DocumentTabViewModel.Settings = _store;
    }

    void IDisposable.Dispose() => DocumentTabViewModel.Settings = null;

    private async Task<(DocumentTabViewModel Tab, AnnotationPropertiesViewModel Vm, PageId Page)> SetupWithAsync(params Annotation[] annots)
    {
        string p = Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".pdf");
        TestPdfFactory.CreateLabeled(p, [(400, 600)]);
        var tab = await OpenTab(p);
        var page = tab.Session!.Pages[0].Id;
        foreach (var a in annots)
            await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Add(page, a with { Id = tab.Session.Annotations.NewId() }));
        tab.Tool = ToolKind.AnnotSelect;
        return (tab, new AnnotationPropertiesViewModel(tab, () => Fixed), page);
    }

    private static Annotation Base(AnnotKind kind) => new()
    {
        Kind = kind, Color = PdfColor.FromHex("#E5372D"), BorderWidth = 1.5f, Opacity = 1f, Author = "A", Flags = PdfAnnotFlags.Print,
        CreatedAt = Fixed.AddDays(-1), ModifiedAt = Fixed.AddDays(-1),
    };

    private static Annotation LineAnnot() => Base(AnnotKind.Line) with
    {
        Vertices = [new PdfPointF(20, 100), new PdfPointF(120, 100)], Rect = new PdfRectF(20, 95, 120, 105),
    };

    private static Annotation CalloutAnnot()
    {
        var box = new PdfRectF(150, 300, 250, 360);
        var a = Base(AnnotKind.FreeText) with
        {
            Intent = FreeTextIntent.Callout, TextBox = box, Vertices = [new PdfPointF(40, 330), new PdfPointF(150, 330)],
            EndEnding = LineEnding.OpenArrow, Contents = "c", FontSizePt = 12f, TextColor = PdfColor.FromHex("#000000"),
        };
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private static Annotation Current(DocumentTabViewModel tab, PageId page, AnnotId id) =>
        tab.Session!.Annotations.Get(page).Items.Single(v => v.Data.Id == id).Data;

    [Fact]
    public async Task LineStyle_Choices_ShownForLineKindsOnly_AndApplied()
    {
        var (tab, vm, page) = await SetupWithAsync(LineAnnot(), Base(AnnotKind.Square) with { Rect = new PdfRectF(200, 100, 260, 160) });
        var items = tab.Session!.Annotations.Get(page).Items.Select(v => v.Data).ToList();
        var line = items.Single(a => a.Kind == AnnotKind.Line);
        var square = items.Single(a => a.Kind == AnnotKind.Square);
        Assert.Equal(4, AnnotationPropertiesViewModel.LineStyleChoices.Count);
        tab.Selection.Set(page, [line.Id]);
        Assert.True(vm.ShowLineStyle);
        Assert.Equal(LineStyle.Solid, vm.LineStyle);
        tab.Selection.Set(page, [square.Id]);
        Assert.False(vm.ShowLineStyle);

        tab.Selection.Set(page, [line.Id]);
        await vm.SetLineStyleAsync(LineStyle.DashDot);
        var after = Current(tab, page, line.Id);
        Assert.Equal(LineStyle.DashDot, after.LineStyle);
        Assert.Equal(line with { LineStyle = LineStyle.DashDot, ModifiedAt = after.ModifiedAt }, after);   // 바뀐 속성 하나만
        await tab.Session.Commands.UndoAsync();
        Assert.Equal(LineStyle.Solid, Current(tab, page, line.Id).LineStyle);   // 명령 하나 = 한 번에 되돌아간다
    }

    [Fact]
    public async Task LineStyle_Names_AreAllFilled()
    {
        foreach (var s in AnnotationPropertiesViewModel.LineStyleChoices)
            Assert.False(string.IsNullOrWhiteSpace(AnnotationPropertiesViewModel.LineStyleName(s)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BothArrows_SetsBothEndings_OneUndo()
    {
        var (tab, vm, page) = await SetupWithAsync(LineAnnot());
        var line = tab.Session!.Annotations.Get(page).Items.Single().Data;
        tab.Selection.Set(page, [line.Id]);
        await vm.SetBothArrowsAsync();
        var after = Current(tab, page, line.Id);
        Assert.Equal(LineEnding.OpenArrow, after.StartEnding);
        Assert.Equal(LineEnding.OpenArrow, after.EndEnding);
        await tab.Session.Commands.UndoAsync();
        var undone = Current(tab, page, line.Id);
        Assert.Equal(LineEnding.None, undone.StartEnding);
        Assert.Equal(LineEnding.None, undone.EndEnding);
    }

    [Fact]
    public async Task Leader_AddRemove_ButtonsStateAndSingleUndo()
    {
        var (tab, vm, page) = await SetupWithAsync(CalloutAnnot(), LineAnnot());
        var items = tab.Session!.Annotations.Get(page).Items.Select(v => v.Data).ToList();
        var co = items.Single(a => a.Kind == AnnotKind.FreeText);
        var line = items.Single(a => a.Kind == AnnotKind.Line);

        tab.Selection.Set(page, [line.Id]);
        Assert.False(vm.ShowLeaders);                      // 설명선이 아니면 지시선 단추가 없다
        tab.Selection.Set(page, [co.Id]);
        Assert.True(vm.ShowLeaders);
        Assert.True(vm.CanAddLeader);
        Assert.False(vm.CanRemoveLeader);

        await vm.AddSecondLeaderAsync();
        var two = Current(tab, page, co.Id);
        Assert.Equal(2, CalloutLeaders.Count(two));
        Assert.True(two.Rect.Contains(two.Leader2[0]));
        Assert.False(vm.CanAddLeader);                     // 최대 2개
        Assert.True(vm.CanRemoveLeader);

        await vm.AddSecondLeaderAsync();                   // 이미 둘이면 아무것도 안 한다(명령 없음)
        Assert.Equal(two, Current(tab, page, co.Id));

        await vm.RemoveSecondLeaderAsync();
        Assert.Equal(1, CalloutLeaders.Count(Current(tab, page, co.Id)));
        await tab.Session.Commands.UndoAsync();            // 삭제 한 번 = 되돌리기 한 번
        Assert.Equal(2, CalloutLeaders.Count(Current(tab, page, co.Id)));
        await tab.Session.Commands.UndoAsync();            // 추가 한 번 = 되돌리기 한 번
        Assert.Equal(1, CalloutLeaders.Count(Current(tab, page, co.Id)));
    }

    [Fact]
    public async Task Style_SaveApply_CarriesLineStyle_AndOldStylesStayValid()
    {
        var (tab, vm, page) = await SetupWithAsync(LineAnnot(), LineAnnot() with { LineStyle = LineStyle.Dashed, BorderWidth = 3f });
        var items = tab.Session!.Annotations.Get(page).Items.Select(v => v.Data).ToList();
        var solid = items.First(a => a.LineStyle == LineStyle.Solid);
        var dashed = items.First(a => a.LineStyle == LineStyle.Dashed);
        var styles = new AnnotationStylesViewModel(tab, vm, () => Fixed);

        tab.Selection.Set(page, [dashed.Id]);
        Assert.True(styles.SaveCurrentAs("파선 3pt"));
        var saved = styles.All.Single(s => s.Name == "파선 3pt");
        Assert.Equal("Dashed", saved.LineStyle);

        tab.Selection.Set(page, [solid.Id]);
        await styles.ApplyAsync(saved);
        Assert.Equal(LineStyle.Dashed, Current(tab, page, solid.Id).LineStyle);

        // 옛 설정 파일의 스타일(LineStyle 없음)은 선 모양을 건드리지 않는다
        var old = new AnnotationStyle("옛", null, "#E5372D", null, 1f, 2f);
        Assert.Null(old.LineStyle);
        tab.Selection.Set(page, [dashed.Id]);
        await styles.ApplyAsync(old);
        Assert.Equal(LineStyle.Dashed, Current(tab, page, dashed.Id).LineStyle);
    }
}
