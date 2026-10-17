using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document.Annotations;
using PdfEditor.Exchange.Xfdf;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

/// <summary>XFDF 가져오기(M3b Task 6) 앱 쪽: 파일 → 명령 하나(실행 취소 한 번), 건너뜀 보고, 오류 문구.</summary>
public class XfdfImporterTests : PanelTestBase
{
    private const string Header = "<?xml version=\"1.0\"?><xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>";

    private async Task<(DocumentTabViewModel Tab, string XfdfPath)> Setup(string annots)
    {
        string pdf = Path.Combine(Dir, "doc.pdf");
        TestPdfFactory.CreateBlank(pdf, 2, 300, 300);
        var tab = await OpenTab(pdf);
        await tab.Session!.AnnotationsReady;
        string x = Path.Combine(Dir, "in.xfdf");
        await File.WriteAllTextAsync(x, Header + annots + "</annots></xfdf>");
        return (tab, x);
    }

    private static int Count(DocumentTabViewModel t)
        => t.Session!.Pages.Sum(p => t.Session.Annotations.Get(p.Id).Items.Length);

    [Fact]
    public async Task Import_ReportsImportedAndSkipped_UndoneInOneStep()
    {
        var (tab, x) = await Setup("""
            <square page="0" rect="0,0,10,10" name="a"/>
            <circle page="1" rect="0,0,10,10" name="b"/>
            <square page="9" rect="0,0,10,10" name="far"/>
            <widget page="0" rect="0,0,1,1"/>
            """);
        var o = await XfdfImporter.ImportAsync(tab, AnnotationAssets.Empty, x);
        Assert.Equal(2, o.Imported);
        Assert.Equal(2, o.SkippedCount);
        Assert.Equal(2, o.Details.Count);
        Assert.Equal(2, Count(tab));
        Assert.Equal("주석 2개를 가져왔습니다. 건너뜀 2개", string.Format(Strings.Msg_XfdfImported, o.Imported, o.SkippedCount));

        await tab.Session!.Commands.UndoAsync();
        Assert.Equal(0, Count(tab));
    }

    [Fact]
    public async Task Import_SameFileTwice_NoDuplicates()
    {
        var (tab, x) = await Setup("<square page=\"0\" rect=\"0,0,10,10\" name=\"a\"/><text page=\"0\" rect=\"0,0,10,10\" name=\"t\"/>");
        await XfdfImporter.ImportAsync(tab, AnnotationAssets.Empty, x);
        var o = await XfdfImporter.ImportAsync(tab, AnnotationAssets.Empty, x);
        Assert.Equal(2, o.Imported);
        Assert.Equal(2, Count(tab));
    }

    [Fact]
    public async Task Import_NotXfdf_ThrowsFormatException_WithFriendlyMessage()
    {
        var (tab, x) = await Setup("");
        await File.WriteAllTextAsync(x, "garbage <<<");
        var ex = await Assert.ThrowsAsync<XfdfFormatException>(() => XfdfImporter.ImportAsync(tab, AnnotationAssets.Empty, x));
        Assert.Equal(Strings.Err_XfdfInvalid, ErrorMessages.For(ex));
        Assert.Equal(0, Count(tab));
    }

    [Fact]
    public async Task Import_TooLarge_FriendlyMessage()
    {
        Assert.Equal(Strings.Err_XfdfTooLarge, ErrorMessages.For(new XfdfFormatException(XfdfFailure.TooLarge, "big")));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Import_EncryptedDocument_ReportsSkippedCount()
    {
        string pdf = Path.Combine(Dir, "enc.pdf");
        TestPdfFactory.CreateEncrypted(pdf, 1, 300, 300, "secret");
        var tab = await OpenTab(pdf, "secret");
        await tab.Session!.AnnotationsReady;
        string x = Path.Combine(Dir, "enc.xfdf");
        await File.WriteAllTextAsync(x, Header + "<square page=\"0\" rect=\"0,0,10,10\" name=\"a\"/><freetext page=\"0\" rect=\"0,0,10,10\" name=\"b\"><contents>x</contents></freetext></annots></xfdf>");
        var o = await XfdfImporter.ImportAsync(tab, AnnotationAssets.Empty, x);
        Assert.Equal(1, o.Imported);
        Assert.Equal(1, o.SkippedCount);
        Assert.Contains("encrypted", o.Details[0]);
    }
}
