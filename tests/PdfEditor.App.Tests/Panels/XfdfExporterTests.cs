using System.Xml.Linq;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

/// <summary>XFDF 내보내기(M3b Task 5): 문서의 모든 주석 + 읽기 전용 원본 정보, 암호 문서에서도 동작.</summary>
public class XfdfExporterTests : PanelTestBase
{
    private static readonly XNamespace Ns = "http://ns.adobe.com/xfdf/";

    [Fact]
    public async Task Export_MixedFixture_WritesEditableAndReadOnlyOriginals()
    {
        string pdf = Path.Combine(Dir, "mixed.pdf");
        AnnotatedPdfFactory.CreateMixed(pdf);
        var tab = await OpenTab(pdf);
        await tab.Session!.AnnotationsReady;

        string outPath = Path.Combine(Dir, "out.xfdf");
        int n = await XfdfExporter.ExportAsync(tab.Session, AnnotationAssets.Empty, outPath);

        var doc = XDocument.Load(outPath);
        var els = doc.Root!.Element(Ns + "annots")!.Elements().ToList();
        Assert.Equal(n, els.Count);
        Assert.True(n >= 10);
        Assert.Equal("mixed.pdf", (string?)doc.Root.Element(Ns + "f")!.Attribute("href"));
        Assert.DoesNotContain(els, e => e.Name.LocalName == "link");
        Assert.Empty(Directory.GetFiles(Dir, "*.tmp"));
    }

    [Fact]
    public async Task Export_EncryptedDocument_Works()
    {
        string pdf = Path.Combine(Dir, "enc.pdf");
        TestPdfFactory.CreateEncrypted(pdf, 1, 400, 600, "secret");
        var tab = await OpenTab(pdf, "secret");
        await tab.Session!.AnnotationsReady;
        var a = new Annotation { Id = tab.Session.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(10, 10, 50, 40), Color = new PdfColor(1, 2, 3), Contents = "enc" };
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Add(tab.Session.Pages[0].Id, a));

        string outPath = Path.Combine(Dir, "enc.xfdf");
        int n = await XfdfExporter.ExportAsync(tab.Session, AnnotationAssets.Empty, outPath);
        Assert.Equal(1, n);
        Assert.Equal("enc", XDocument.Load(outPath).Descendants(Ns + "contents").Single().Value);
    }
}
