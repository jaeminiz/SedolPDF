using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

/// <summary>0.5.0: 속성 패널·형식 탭 모델의 새 글자 서식(굵게/기울임/밑줄/취소선/글꼴)과 이름 붙인 스타일 저장·적용.</summary>
[Collection("TabSettings")]
public class V050StylePropertiesTests : PanelTestBase, IDisposable
{
    private static readonly DateTimeOffset Fixed = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private readonly SettingsStore _store;

    public V050StylePropertiesTests()
    {
        Directory.CreateDirectory(Dir);
        _store = new SettingsStore(Path.Combine(Dir, "settings.json"));
        DocumentTabViewModel.Settings = _store;
    }

    void IDisposable.Dispose() => DocumentTabViewModel.Settings = null;

    private async Task<(DocumentTabViewModel Tab, AnnotationPropertiesViewModel Vm, AnnotationStylesViewModel Styles, PageId Page)> SetupAsync(params Annotation[] annots)
    {
        string p = Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".pdf");
        TestPdfFactory.CreateLabeled(p, [(400, 600)]);
        var tab = await OpenTab(p);
        var page = tab.Session!.Pages[0].Id;
        foreach (var a in annots)
            await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Add(page, a with { Id = tab.Session.Annotations.NewId() }));
        tab.Tool = ToolKind.AnnotSelect;
        return (tab, new AnnotationPropertiesViewModel(tab, () => Fixed), new AnnotationStylesViewModel(tab, null, () => Fixed), page);
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

    private static Annotation Box(string text = "글자") => Base(AnnotKind.FreeText) with
    {
        Intent = FreeTextIntent.TextBox, Rect = new PdfRectF(20, 300, 220, 360), Contents = text, FontSizePt = 12f, TextColor = PdfColor.FromHex("#000000"),
    };

    private static Annotation CalloutAnnot()
    {
        var a = Base(AnnotKind.FreeText) with
        {
            Intent = FreeTextIntent.Callout, TextBox = new PdfRectF(150, 300, 250, 360), Vertices = [new PdfPointF(40, 330), new PdfPointF(150, 330)],
            EndEnding = LineEnding.OpenArrow, Contents = "c", FontSizePt = 12f, TextColor = PdfColor.FromHex("#000000"),
        };
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private static Annotation Current(DocumentTabViewModel tab, PageId page, AnnotId id) =>
        tab.Session!.Annotations.Get(page).Items.Single(v => v.Data.Id == id).Data;

    [Fact]
    public async Task TextFormat_Toggles_AreIndependent_AndEachIsOneUndo()
    {
        var (tab, vm, _, page) = await SetupAsync(Box());
        var box = tab.Session!.Annotations.Get(page).Items.Single().Data;
        tab.Selection.Set(page, [box.Id]);
        Assert.True(vm.ShowText);
        Assert.False(vm.Bold);

        await vm.SetTextStyleAsync(TextFormat.BoldBit, true);
        await vm.SetTextStyleAsync(TextFormat.UnderlineBit, true);
        var a = Current(tab, page, box.Id);
        Assert.True(a.Bold && a.Underline && !a.Italic && !a.StrikeThrough);
        Assert.True(vm.Bold && vm.Underline && !vm.Italic);

        await vm.SetTextStyleAsync(TextFormat.BoldBit, false);
        a = Current(tab, page, box.Id);
        Assert.True(!a.Bold && a.Underline);
        await vm.SetTextStyleAsync(TextFormat.StrikeBit, true);
        await vm.SetTextStyleAsync(TextFormat.ItalicBit, true);
        await vm.SetFontFamilyAsync(TextFontFamily.Serif);
        a = Current(tab, page, box.Id);
        Assert.True(a.Italic && a.StrikeThrough && a.Underline && a.TextFamily == TextFontFamily.Serif);

        await tab.Session.Commands.UndoAsync();   // 글꼴만 되돌아간다
        a = Current(tab, page, box.Id);
        Assert.Equal(TextFontFamily.Sans, a.TextFamily);
        Assert.True(a.Italic);

        // 이미 켜진 것을 다시 켜도 명령을 만들지 않는다(실행 취소 한 칸을 낭비하지 않음)
        var before = Current(tab, page, box.Id);
        await vm.SetTextStyleAsync(TextFormat.ItalicBit, true);
        Assert.Same(before, Current(tab, page, box.Id));
    }

    [Fact]
    public async Task TextFormat_NotShownOrApplied_ForNonFreeText()
    {
        var (tab, vm, _, page) = await SetupAsync(LineAnnot());
        var line = tab.Session!.Annotations.Get(page).Items.Single().Data;
        tab.Selection.Set(page, [line.Id]);
        Assert.False(vm.ShowText);
        await vm.SetTextStyleAsync(TextFormat.BoldBit, true);
        await vm.SetFontFamilyAsync(TextFontFamily.Serif);
        Assert.Equal(line, Current(tab, page, line.Id));
    }

    [Fact]
    public async Task NamedStyle_SavesAndAppliesTextFormat_AndLineStyleNames()
    {
        var (tab, _, styles, page) = await SetupAsync(Box("A") with { Bold = true, Underline = true, TextFamily = TextFontFamily.Serif }, Box("B"));
        var items = tab.Session!.Annotations.Get(page).Items.Select(v => v.Data).ToList();
        var src = items.Single(a => a.Contents == "A");
        var dst = items.Single(a => a.Contents == "B");

        tab.Selection.Set(page, [src.Id]);
        Assert.True(styles.SaveCurrentAs("서식 스타일"));
        var saved = styles.All.Single(s => s.Name == "서식 스타일");
        Assert.Equal("Serif", saved.TextFont);
        Assert.Equal(TextFormat.BoldBit | TextFormat.UnderlineBit, saved.TextFormat);

        tab.Selection.Set(page, [dst.Id]);
        await styles.ApplyAsync(saved);
        var after = Current(tab, page, dst.Id);
        Assert.True(after.Bold && after.Underline && !after.Italic);
        Assert.Equal(TextFontFamily.Serif, after.TextFamily);

    }

    [Fact]
    public async Task OldStyleWithoutTextFields_DoesNotTouchFormat()
    {
        var (tab, _, styles, page) = await SetupAsync(Box("A") with { Italic = true });
        var a = tab.Session!.Annotations.Get(page).Items.Single().Data;
        tab.Selection.Set(page, [a.Id]);
        await styles.ApplyAsync(new AnnotationStyle("old", null, "#005FB8", null, 1f, 1f, 14f));
        var after = Current(tab, page, a.Id);
        Assert.True(after.Italic);
        Assert.Equal(14f, after.FontSizePt);
    }
}
