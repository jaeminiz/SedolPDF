using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Settings;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

public class AnnotationPropertiesViewModelTests : PanelTestBase, IDisposable
{
    private static readonly PdfColor Red = PdfColor.FromHex("#E5372D");
    private static readonly PdfColor Blue = PdfColor.FromHex("#005FB8");
    private static readonly PdfColor Green = PdfColor.FromHex("#1F9D55");
    private static readonly DateTimeOffset Fixed = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
    private readonly SettingsStore _store;

    public AnnotationPropertiesViewModelTests()
    {
        Directory.CreateDirectory(Dir);
        _store = new SettingsStore(Path.Combine(Dir, "settings.json"));
        DocumentTabViewModel.Settings = _store;
    }

    void IDisposable.Dispose() => DocumentTabViewModel.Settings = null;

    private async Task<(DocumentTabViewModel Tab, AnnotationPropertiesViewModel Vm, PageId Page, List<Annotation> Items)> SetupAsync(
        params (AnnotKind Kind, PdfColor Color)[] kinds)
    {
        string p = Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".pdf");
        TestPdfFactory.CreateLabeled(p, [(400, 600)]);
        var tab = await OpenTab(p);
        var page = tab.Session!.Pages[0].Id;
        var items = new List<Annotation>();
        int i = 0;
        foreach (var (kind, color) in kinds)
        {
            var a = new Annotation
            {
                Id = tab.Session.Annotations.NewId(), Kind = kind, Rect = new PdfRectF(10 + i * 70, 10, 70 + i * 70, 60),
                Color = color, BorderWidth = 1f, Opacity = 1f, Author = "A", Flags = PdfAnnotFlags.Print,
                CreatedAt = Fixed.AddDays(-1), ModifiedAt = Fixed.AddDays(-1),
            };
            await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Add(page, a));
            items.Add(a);
            i++;
        }
        tab.Tool = ToolKind.AnnotSelect;
        var vm = new AnnotationPropertiesViewModel(tab, () => Fixed);
        return (tab, vm, page, items);
    }

    private static Annotation Current(DocumentTabViewModel tab, PageId page, AnnotId id) =>
        tab.Session!.Annotations.Get(page).Items.Single(v => v.Data.Id == id).Data;

    [Fact]
    public async Task TwoSelectedDifferentColors_ColorMixed()
    {
        var (tab, vm, page, items) = await SetupAsync((AnnotKind.Square, Red), (AnnotKind.Circle, Blue));
        tab.Selection.Set(page, [items[0].Id, items[1].Id]);
        Assert.Equal(AnnotationPropertiesMode.Selection, vm.Mode);
        Assert.Null(vm.Color);
        Assert.True(vm.IsColorMixed);
        Assert.False(vm.CanEditAuthor);
    }

    [Fact]
    public async Task SwatchClick_OneCommand_UndoRestoresBoth()
    {
        var (tab, vm, page, items) = await SetupAsync((AnnotKind.Square, Red), (AnnotKind.Circle, Blue));
        tab.Selection.Set(page, [items[0].Id, items[1].Id]);
        await vm.SetColorAsync(Green);
        Assert.All(items, a => Assert.Equal(Green, Current(tab, page, a.Id).Color));
        Assert.All(items, a => Assert.Equal(Fixed, Current(tab, page, a.Id).ModifiedAt));
        await tab.Session!.Commands.UndoAsync();
        Assert.Equal(Red, Current(tab, page, items[0].Id).Color);
        Assert.Equal(Blue, Current(tab, page, items[1].Id).Color);
    }

    [Fact]
    public async Task WidthSlider_PreviewThenCommit_SingleCommand()
    {
        var (tab, vm, page, items) = await SetupAsync((AnnotKind.Square, Red));
        tab.Selection.Set(page, [items[0].Id]);
        IReadOnlyDictionary<AnnotId, Annotation?>? seen = null;
        tab.AnnotationPreviewRequested += (_, d) => seen = d;
        vm.BeginPreview();
        vm.Width = 2f;
        vm.Width = 4f;
        Assert.Equal(4f, vm.Previews![items[0].Id]!.BorderWidth);
        Assert.Equal(4f, seen![items[0].Id]!.BorderWidth);
        Assert.Equal(1f, Current(tab, page, items[0].Id).BorderWidth);   // 아직 명령 없음
        await vm.CommitPreviewAsync();
        Assert.Equal(4f, Current(tab, page, items[0].Id).BorderWidth);
        Assert.Null(seen);
        await tab.Session!.Commands.UndoAsync();
        Assert.Equal(1f, Current(tab, page, items[0].Id).BorderWidth);   // 한 번에 원래대로
    }

    [Fact]
    public async Task Opacity_ClampedTo10To100()
    {
        var (tab, vm, page, items) = await SetupAsync((AnnotKind.Square, Red));
        tab.Selection.Set(page, [items[0].Id]);
        vm.BeginPreview();
        vm.OpacityPercent = 3;
        Assert.Equal(10, vm.OpacityPercent);
        vm.OpacityPercent = 250;
        Assert.Equal(100, vm.OpacityPercent);
        vm.OpacityPercent = 47;
        Assert.Equal(45, vm.OpacityPercent);
        vm.Width = 99f;
        Assert.Equal(12f, vm.Width);
        await vm.CommitPreviewAsync();
        Assert.Equal(0.45f, Current(tab, page, items[0].Id).Opacity, 3);
    }

    [Fact]
    public async Task ShowFillAndWidth_ByKind()
    {
        var (tab, vm, page, items) = await SetupAsync((AnnotKind.Highlight, Red), (AnnotKind.Square, Red), (AnnotKind.Ink, Red));
        tab.Selection.Set(page, [items[0].Id]);
        Assert.False(vm.ShowFill); Assert.False(vm.ShowWidth);
        tab.Selection.Set(page, [items[1].Id]);
        Assert.True(vm.ShowFill); Assert.True(vm.ShowWidth);
        tab.Selection.Set(page, [items[2].Id]);
        Assert.False(vm.ShowFill); Assert.True(vm.ShowWidth);
    }

    [Fact]
    public async Task ToolDefaultsMode_WritesSettingsNotCommands()
    {
        var (tab, vm, _, _) = await SetupAsync();
        tab.Tool = ToolKind.Rectangle;
        Assert.Equal(AnnotationPropertiesMode.ToolDefaults, vm.Mode);
        Assert.True(vm.ShowFill);
        await vm.SetColorAsync(Blue);
        await vm.SetFillAsync(Green);
        vm.BeginPreview();
        vm.Width = 3f;
        vm.OpacityPercent = 60;
        await vm.CommitPreviewAsync();
        Assert.False(tab.Session!.Commands.CanUndo);
        var st = DocumentTabViewModel.StyleFor(ToolKind.Rectangle);
        Assert.Equal(Blue, st.Color);
        Assert.Equal(Green, st.Fill);
        Assert.Equal(3f, st.Width);
        Assert.Equal(0.6f, st.Opacity, 3);
        Assert.Equal(ToolDefaults.For(ToolKind.Pencil), DocumentTabViewModel.StyleFor(ToolKind.Pencil));   // 다른 도구는 그대로
    }

    [Fact]
    public async Task AuthorEdit_SingleSelection_Command()
    {
        var (tab, vm, page, items) = await SetupAsync((AnnotKind.Square, Red));
        tab.Selection.Set(page, [items[0].Id]);
        Assert.True(vm.CanEditAuthor);
        Assert.Equal("A", vm.Author);
        vm.Author = "  Kim  ";
        await vm.CommitAuthorAsync();
        var now = Current(tab, page, items[0].Id);
        Assert.Equal("Kim", now.Author);
        Assert.Equal(Fixed, now.ModifiedAt);
        await tab.Session!.Commands.UndoAsync();
        Assert.Equal("A", Current(tab, page, items[0].Id).Author);
    }

    [Fact]
    public async Task NoSelectionAndNoCreateTool_ModeNone()
    {
        var (tab, vm, _, _) = await SetupAsync();
        Assert.Equal(AnnotationPropertiesMode.None, vm.Mode);
        tab.Tool = ToolKind.Eraser;
        Assert.Equal(AnnotationPropertiesMode.None, vm.Mode);
    }

    [Fact]
    public async Task Author_UsesSettingThenUserName_AndCreationUsesPersistedStyle()
    {
        var (tab, _, _, _) = await SetupAsync();
        Assert.Equal(Environment.UserName, DocumentTabViewModel.CurrentAuthor);
        _store.Save(_store.Load() with { AuthorName = "Park" });
        Assert.Equal("Park", DocumentTabViewModel.CurrentAuthor);
        DocumentTabViewModel.SaveToolStyle(ToolKind.Rectangle, new ToolStyle(Blue, null, 1f, 3f));
        var page = tab.Session!.Pages[0].Id;
        tab.Tools.Begin(ToolKind.Rectangle, DocumentTabViewModel.StyleFor(ToolKind.Rectangle), page, new PdfPointF(10, 10), 1f);
        tab.Tools.Move(new PdfPointF(90, 90));
        var cmd = tab.Tools.End(new PdfPointF(90, 90));
        await tab.RunAnnotationCommandAsync(cmd!);
        var a = tab.Session.Annotations.Get(page).Items.Single().Data;
        Assert.Equal("Park", a.Author);
        Assert.Equal(Blue, a.Color);
        Assert.Equal(3f, a.BorderWidth);
    }
}
