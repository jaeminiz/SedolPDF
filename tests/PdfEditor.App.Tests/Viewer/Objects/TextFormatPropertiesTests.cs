using PdfEditor.App.Documents;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.TestFixtures;
using Pdf.Native;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Objects;

/// <summary>0.6.0 글자 형식: 원본 글 객체를 속성 패널 VM으로 읽고(글꼴·크기·색) 색·크기는 원래 글꼴을 유지한 채 바꾼다.</summary>
[Collection("TabSettings")]
public class TextFormatPropertiesTests : ObjectUiTestBase
{
    private async Task<(DocumentTabViewModel Tab, PdfEditor.Document.DocumentSession S, PageObjectEntry Line)> SelectArialAsync()
    {
        var s = await OpenAsync(p => ContentPdfFactory.CreateDocumentLike(p));
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        var page = s.Pages[0].Id;
        var line = await s.Thread.InvokeAsync(() => s.GetObjectIndex(page).Entries.First(e => e.TextPreview == ContentPdfFactory.LineArial));
        tab.Objects.Select(new SelectedObject(page, line.Ref, PdfPageObjectType.Text, line.Bounds));
        PumpUi(() => tab.ObjectProps.HasTextFormat);
        return (tab, s, line);
    }

    [Fact]
    public async Task Select_OriginalText_ShowsFontSizeColor()
    {
        var (tab, _, _) = await SelectArialAsync();
        var f = tab.ObjectProps.Format!;
        Assert.Equal(14f, f.SizePt, 0.1f);
        Assert.True(f.IsOriginalFont);
        Assert.Contains("Arial", f.FontName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetColor_OriginalText_KeepsFont_OneEdit()
    {
        var (tab, s, line) = await SelectArialAsync();
        var red = new PdfColor(255, 0, 0);
        await tab.ObjectProps.SetTextColorAsync(red);
        var edits = s.Content.For(s.Pages[0].Id);
        var tc = Assert.IsType<SetTextColorEdit>(Assert.Single(edits));
        Assert.Equal(red, tc.Color);
        Assert.Equal(line.Ref, tc.Target);
        await s.Commands.UndoAsync();
        Assert.Empty(s.Content.For(s.Pages[0].Id));
    }

    [Fact]
    public async Task SetColor_OverlappedFauxBold_ChangesAllCopies()
    {
        var s = await OpenAsync(p => ContentPdfFactory.CreateFauxBoldLine(p));
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        var page = s.Pages[0].Id;
        var e = await s.Thread.InvokeAsync(() => s.GetObjectIndex(page).Entries.First(x => x.TextPreview == ContentPdfFactory.LineFauxBold));
        tab.Objects.Select(new SelectedObject(page, e.Ref, PdfPageObjectType.Text, e.Bounds));
        PumpUi(() => tab.ObjectProps.HasTextFormat);
        await tab.ObjectProps.SetTextColorAsync(new PdfColor(255, 0, 0));
        Assert.Equal(4, s.Content.For(page).OfType<SetTextColorEdit>().Count());   // 겹쳐 찍은 4개 모두: 가장자리에 옛 색이 남지 않는다
    }

    [Fact]
    public async Task SetSize_OriginalText_ScalesAboutBaselineOrigin()
    {
        var (tab, s, line) = await SelectArialAsync();
        await tab.ObjectProps.SetTextSizeAsync(28f);
        var t = Assert.IsType<TransformEdit>(Assert.Single(s.Content.For(s.Pages[0].Id)));
        Assert.Equal(2f, t.Then.A, 0.001f);
        Assert.Equal(2f, t.Then.D, 0.001f);
        Assert.Equal(60f, 2f * 60f + t.Then.E, 0.05f);   // 줄 시작점(60pt)은 그대로
        Assert.Equal(line.Ref, t.Target);
    }

    [Fact]
    public async Task SetBold_OriginalText_RewritesWithAppFontBold()
    {
        var (tab, s, _) = await SelectArialAsync();
        await tab.ObjectProps.SetTextEmphasisAsync(bold: true);
        var app = Assert.Single(s.Content.For(s.Pages[0].Id).OfType<InsertAppTextEdit>());
        Assert.True(app.Line.BoldStrokePt > 0);
        Assert.Equal(ContentPdfFactory.LineArial, app.Line.Text);
    }
}
