using PdfEditor.Document.Layers;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests;

public class LayerReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-layer-" + Guid.NewGuid().ToString("N"));

    public LayerReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void LayerReader_ReadsNamesAndDefaultOff()
    {
        string p = P("layers.pdf");
        TestPdfFactory.CreateWithLayers(p, ["배관", "전기"]);

        // 두 번째 레이어를 /D /OFF 목록에 넣는다.
        using (var doc = PdfReader.Open(p, PdfDocumentOpenMode.Modify))
        {
            var ocProps = doc.Internals.Catalog.Elements.GetDictionary("/OCProperties")!;
            var ocgs = ocProps.Elements.GetArray("/OCGs")!;
            var d = ocProps.Elements.GetDictionary("/D")!;
            d.Elements["/OFF"] = new PdfArray(doc, ((PdfReference)ocgs.Elements[1]));
            doc.Save(p);
        }

        var layers = LayerReader.Read(File.ReadAllBytes(p), null);

        Assert.Equal([new LayerInfo("배관", true), new LayerInfo("전기", false)], layers);
    }

    [Fact]
    public void LayerReader_NoOcProperties_Empty()
    {
        string p = P("plain.pdf");
        TestPdfFactory.CreateBlank(p, 2, 595, 842);
        Assert.Empty(LayerReader.Read(File.ReadAllBytes(p), null));
    }

    [Fact]
    public void LayerReader_GarbageBytes_Throws()
    {
        Assert.ThrowsAny<Exception>(() => LayerReader.Read([1, 2, 3, 4], null));
    }

    [Fact]
    public void LayerReader_UserPasswordPdf_Probe()
    {
        // 암호 PDF를 사용자 암호로 Import 모드에서 열 수 있는지 확인한다 (V6에서 Modify는 소유자 암호가 필요했음).
        string p = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(p, 1, 595, 842, "pw1");
        var layers = LayerReader.Read(File.ReadAllBytes(p), "pw1");
        Assert.Empty(layers);   // 열리고, 레이어는 없음
    }
}
