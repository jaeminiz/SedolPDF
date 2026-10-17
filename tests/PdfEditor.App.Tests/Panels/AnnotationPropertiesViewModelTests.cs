using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Settings;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

[Collection("TabSettings")]
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

    private static Annotation WithWidth(Annotation a, float w) => a with { BorderWidth = w };

    private async Task<(DocumentTabViewModel Tab, AnnotationPropertiesViewModel Vm, PageId Page, List<Annotation> Items)> TwoWidthsAsync()
    {
        var r = await SetupAsync((AnnotKind.Square, Red), (AnnotKind.Circle, Red));
        var (tab, _, page, items) = r;
        // 두 번째 주석의 두께만 다르게 (명령으로 바꿔 모델과 일치시킨다)
        var changed = WithWidth(items[1], 5f);
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Replace(page, [(items[1], changed)]));
        items[1] = changed;
        tab.Selection.Set(page, [items[0].Id, items[1].Id]);
        return r;
    }

    [Fact]
    public async Task OpacityEdit_MultiSelection_LeavesWidthsUntouched()
    {
        var (tab, vm, page, items) = await TwoWidthsAsync();
        vm.BeginPreview();
        vm.OpacityPercent = 50;
        await vm.CommitPreviewAsync();
        Assert.Equal(1f, Current(tab, page, items[0].Id).BorderWidth);
        Assert.Equal(5f, Current(tab, page, items[1].Id).BorderWidth);
        Assert.All(items, a => Assert.Equal(0.5f, Current(tab, page, a.Id).Opacity, 3));
    }

    [Fact]
    public async Task WidthEdit_MultiSelection_LeavesOpacityUntouched()
    {
        var (tab, vm, page, items) = await TwoWidthsAsync();
        var half = Current(tab, page, items[1].Id) with { Opacity = 0.3f };
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Replace(page, [(Current(tab, page, items[1].Id), half)]));
        vm.Refresh();
        vm.BeginPreview();
        vm.Width = 3f;
        await vm.CommitPreviewAsync();
        Assert.All(items, a => Assert.Equal(3f, Current(tab, page, a.Id).BorderWidth));
        Assert.Equal(1f, Current(tab, page, items[0].Id).Opacity, 3);
        Assert.Equal(0.3f, Current(tab, page, items[1].Id).Opacity, 3);
    }

    [Fact]
    public async Task SliderCommit_ReResolvesCurrentAnnotations_NoStaleBeforeNoResurrection()
    {
        var (tab, vm, page, items) = await TwoWidthsAsync();
        vm.BeginPreview();
        vm.Width = 3f;
        // 끌기 중에 첫 주석이 다른 경로로 바뀌고 둘째는 지워진다
        var moved = Current(tab, page, items[0].Id) with { Rect = new PdfRectF(100, 100, 150, 150) };
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Replace(page, [(Current(tab, page, items[0].Id), moved)]));
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Delete(page, [Current(tab, page, items[1].Id)]));
        await vm.CommitPreviewAsync();
        var a0 = Current(tab, page, items[0].Id);
        Assert.Equal(3f, a0.BorderWidth);
        Assert.Equal(moved.Rect, a0.Rect);   // 바뀐 위치가 유지된다
        Assert.DoesNotContain(tab.Session!.Annotations.Get(page).Items, v => v.Data.Id == items[1].Id);   // 되살아나지 않음
    }

    [Fact]
    public async Task FillMixed_WhenFillsDiffer()
    {
        var (tab, vm, page, items) = await SetupAsync((AnnotKind.Square, Red), (AnnotKind.Square, Red));
        var f = items[1] with { FillColor = Green };
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Replace(page, [(items[1], f)]));
        tab.Selection.Set(page, [items[0].Id, items[1].Id]);
        Assert.True(vm.IsFillMixed);
        Assert.Null(vm.Fill);
    }

    // ---- M3a: 새 유형 속성 ----

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
        Kind = kind, Color = Red, BorderWidth = 1.5f, Opacity = 1f, Author = "A", Flags = PdfAnnotFlags.Print,
        CreatedAt = Fixed.AddDays(-1), ModifiedAt = Fixed.AddDays(-1),
    };

    private static Annotation LineAnnot() => Base(AnnotKind.Line) with
    {
        Vertices = [new PdfPointF(20, 100), new PdfPointF(120, 100)], Rect = new PdfRectF(20, 95, 120, 105),
    };

    private static Annotation FreeTextAnnot() => Base(AnnotKind.FreeText) with
    {
        Intent = FreeTextIntent.TextBox, Rect = new PdfRectF(20, 200, 220, 240), Contents = "hello", FontSizePt = 12f,
        TextColor = PdfColor.FromHex("#000000"),
    };

    private static Annotation StampAnnot() => Base(AnnotKind.Stamp) with
    {
        Rect = new PdfRectF(20, 300, 170, 350), Stamp = DocumentTabViewModel.BuiltinStamp("Approved"), Contents = "x",
    };

    [Fact]
    public async Task Props_LineEndingsShownForLineOnly()
    {
        var (tab, vm, page) = await SetupWithAsync(LineAnnot(), Base(AnnotKind.Square) with { Rect = new PdfRectF(200, 100, 260, 160) });
        var items = tab.Session!.Annotations.Get(page).Items.Select(v => v.Data).ToList();
        var line = items.Single(a => a.Kind == AnnotKind.Line);
        var square = items.Single(a => a.Kind == AnnotKind.Square);
        tab.Selection.Set(page, [line.Id]);
        Assert.True(vm.ShowLineEndings);
        Assert.Equal(8, AnnotationPropertiesViewModel.LineEndingChoices.Count);
        tab.Selection.Set(page, [square.Id]);
        Assert.False(vm.ShowLineEndings);
        Assert.True(vm.ShowCloud);   // 사각형은 클라우드 켜기가 있다
        tab.Selection.Set(page, [line.Id, square.Id]);
        Assert.False(vm.ShowLineEndings);   // 섞이면 숨긴다

        tab.Selection.Set(page, [line.Id]);
        await vm.SetLineEndingAsync(start: false, LineEnding.ClosedArrow);
        var after = tab.Session.Annotations.Get(page).Items.Select(v => v.Data).Single(a => a.Id == line.Id);
        Assert.Equal(LineEnding.ClosedArrow, after.EndEnding);
        Assert.Equal(LineEnding.None, after.StartEnding);
        Assert.Equal(line with { EndEnding = LineEnding.ClosedArrow, ModifiedAt = after.ModifiedAt }, after);   // 바뀐 속성 하나만
    }

    [Fact]
    public async Task Props_FontSizeChange_OneCommand()
    {
        var (tab, vm, page) = await SetupWithAsync(FreeTextAnnot());
        var orig = tab.Session!.Annotations.Get(page).Items.Single().Data;
        tab.Selection.Set(page, [orig.Id]);
        Assert.True(vm.ShowText);
        Assert.Equal(12f, vm.FontSizePt);
        await vm.SetFontSizeAsync(80f);   // 상한 72로 맞춘다
        var after = tab.Session.Annotations.Get(page).Items.Single().Data;
        Assert.Equal(72f, after.FontSizePt);
        Assert.Equal(orig with { FontSizePt = 72f, ModifiedAt = after.ModifiedAt }, after);
        await tab.Session.Commands.UndoAsync();   // 명령 하나 = 한 번에 되돌아간다
        Assert.Equal(12f, tab.Session.Annotations.Get(page).Items.Single().Data.FontSizePt);
        await vm.SetFontSizeAsync(2f);
        Assert.Equal(6f, tab.Session.Annotations.Get(page).Items.Single().Data.FontSizePt);
    }

    [Fact]
    public async Task Props_StampPicker_ChangesStampSpec()
    {
        var (tab, vm, page) = await SetupWithAsync(StampAnnot());
        var orig = tab.Session!.Annotations.Get(page).Items.Single().Data;
        tab.Selection.Set(page, [orig.Id]);
        Assert.True(vm.ShowStamp);
        Assert.Equal("Approved", vm.CurrentStampName);
        Assert.Equal(6, vm.StampNames.Count);

        await vm.SetStampBuiltinAsync("Rejected");
        var a1 = tab.Session.Annotations.Get(page).Items.Single().Data;
        Assert.Equal(StampKind.Builtin, a1.Stamp!.Kind);
        Assert.Equal("Rejected", a1.Stamp.BuiltinName);
        Assert.Equal(PdfEditor.App.Resources.Strings.Stamp_Rejected, a1.Stamp.Text);
        Assert.Equal(orig.Rect, a1.Rect);

        await vm.SetStampTextAsync("  검토본  ");
        var a2 = tab.Session.Annotations.Get(page).Items.Single().Data;
        Assert.Equal(StampKind.Text, a2.Stamp!.Kind);
        Assert.Equal("검토본", a2.Stamp.Text);
    }

    [Fact]
    public async Task Props_StampImage_RejectedFileGivesNotice_NotCrash()
    {
        var (tab, vm, page) = await SetupWithAsync(StampAnnot());
        var orig = tab.Session!.Annotations.Get(page).Items.Single().Data;
        tab.Selection.Set(page, [orig.Id]);
        string? notice = null;
        tab.NoticeRequested += m => notice = m;
        await vm.SetStampImageAsync([1, 2, 3]);   // PNG·JPEG가 아니다
        Assert.Equal(PdfEditor.App.Resources.Strings.Msg_ImageAddFailed, notice);
        Assert.Equal(orig, tab.Session.Annotations.Get(page).Items.Single().Data);
    }

    [Fact]
    public async Task Props_Cloud_OnOffAndIntensity_EachOneCommand()
    {
        var (tab, vm, page) = await SetupWithAsync(Base(AnnotKind.Square) with { Rect = new PdfRectF(20, 100, 120, 160) });
        var orig = tab.Session!.Annotations.Get(page).Items.Single().Data;
        tab.Selection.Set(page, [orig.Id]);
        await vm.SetCloudAsync(true);
        Assert.Equal(1f, tab.Session.Annotations.Get(page).Items.Single().Data.CloudIntensity);
        await vm.SetCloudIntensityAsync(2f);
        Assert.Equal(2f, tab.Session.Annotations.Get(page).Items.Single().Data.CloudIntensity);
        await vm.SetCloudAsync(false);
        Assert.Equal(0f, tab.Session.Annotations.Get(page).Items.Single().Data.CloudIntensity);
    }

    [Fact]
    public async Task Props_AttachmentSave_UsesSanitizedName_AndReportsFailureAsNotice()
    {
        var (tab, vm, page) = await SetupWithAsync();
        // 첨부 주석은 보관소의 파일을 가리킨다 (주석 도구가 쓰는 공유 보관소)
        var assets = new AnnotationAssets();
        tab.Tools.Assets = () => assets;
        string id = assets.AddFile("..\\evil/name.txt", [1, 2, 3]);
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Add(page, Base(AnnotKind.FileAttachment) with
        {
            Id = tab.Session!.Annotations.NewId(), Rect = new PdfRectF(20, 20, 40, 40), AttachmentId = id, AttachmentName = "..\\evil/name.txt",
        }));
        var att = tab.Session!.Annotations.Get(page).Items.Select(v => v.Data).Single(a => a.Kind == AnnotKind.FileAttachment);
        tab.Selection.Set(page, [att.Id]);
        Assert.True(vm.ShowAttachment);
        Assert.True(vm.CanSaveAttachment);

        string? suggested = null;
        string target = Path.Combine(Dir, "saved.bin");
        await vm.SaveSelectedAttachmentAsync(n => { suggested = n; return Task.FromResult<string?>(target); });
        Assert.Equal("name.txt", suggested);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(target));

        string? notice = null;
        tab.NoticeRequested += m => notice = m;
        await vm.SaveSelectedAttachmentAsync(n => Task.FromResult<string?>(Path.Combine(Dir, "no-such-dir", "x.bin")));
        Assert.Equal(PdfEditor.App.Resources.Strings.Msg_AttachmentSaveFailed, notice);
    }
}

[CollectionDefinition("TabSettings", DisableParallelization = true)]
public sealed class TabSettingsCollection;
