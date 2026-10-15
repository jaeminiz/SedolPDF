using System.Drawing;
using System.Text.RegularExpressions;
using Pdf.Native;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace S1.FreeTextKorean;

public class FreeTextApTests : IDisposable
{
    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "s1-" + Guid.NewGuid().ToString("N"));
    private static readonly FreeTextSpec Spec =
        new(0, new RectangleF(72, 72, 300, 60), "가나다 도면 검토 ABC", 14f, 0xFF000000);

    public FreeTextApTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task FreeTextKorean_SavedAp_RendersInPdfium()
    {
        var input = Path.Combine(_dir, "in.pdf");
        var output = Path.Combine(TestSupport.OutDir(), "s1-freetext.pdf");
        await TestSupport.CreateBase(_t, input, squareWithKey: false);
        FreeTextApWriter.Apply(input, output, Spec, TestSupport.LoadFont());

        var withAnn = await TestSupport.NonWhiteRatio(_t, output, 72, 72, 300, 60, true,
            Path.Combine(TestSupport.OutDir(), "s1-freetext-annot.png"), 1.0);
        var without = await TestSupport.NonWhiteRatio(_t, output, 72, 72, 300, 60, false);
        Assert.True(withAnn > 0.02, $"비흰색 비율 {withAnn:P2}");
        Assert.Equal(0.0, without);
    }

    [Fact]
    public async Task FreeTextKorean_Font_IsEmbeddedSubsetWithToUnicode()
    {
        var input = Path.Combine(_dir, "in2.pdf");
        var output = Path.Combine(_dir, "out2.pdf");
        await TestSupport.CreateBase(_t, input, squareWithKey: false);
        FreeTextApWriter.Apply(input, output, Spec, TestSupport.LoadFont());

        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var annot = FindAnnot(doc, "/FreeText");
        var ap = Deref<PdfDictionary>(Deref<PdfDictionary>(annot.Elements["/AP"]).Elements["/N"]);
        var res = Deref<PdfDictionary>(ap.Elements["/Resources"]);
        var font = Deref<PdfDictionary>(Deref<PdfDictionary>(res.Elements["/Font"]).Elements["/F1"]);
        Assert.Equal("/Type0", font.Elements.GetName("/Subtype"));
        Assert.Equal("/Identity-H", font.Elements.GetName("/Encoding"));
        Assert.Matches(new Regex(@"^/[A-Z]{6}\+"), font.Elements.GetName("/BaseFont"));
        Assert.NotNull(font.Elements["/ToUnicode"]);
        var desc = Deref<PdfArray>(font.Elements["/DescendantFonts"]);
        var cid = Deref<PdfDictionary>(desc.Elements[0]);
        var fd = Deref<PdfDictionary>(cid.Elements["/FontDescriptor"]);
        Assert.True(fd.Elements.ContainsKey("/FontFile3") || fd.Elements.ContainsKey("/FontFile2"));
        Assert.True(annot.Elements.ContainsKey("/DA"));
    }

    [Fact]
    public async Task FreeTextKorean_UnknownKeysOfOtherAnnots_Preserved()
    {
        var input = Path.Combine(_dir, "in3.pdf");
        var output = Path.Combine(_dir, "out3.pdf");
        await TestSupport.CreateBase(_t, input, squareWithKey: true);
        FreeTextApWriter.Apply(input, output, Spec, TestSupport.LoadFont());

        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var square = FindAnnot(doc, "/Square");
        Assert.Equal("keep", square.Elements.GetString("/PXC_Test"));
    }

    private static PdfDictionary FindAnnot(PdfSharp.Pdf.PdfDocument doc, string subtype)
    {
        var arr = Deref<PdfArray>(doc.Pages[0].Elements["/Annots"]);
        foreach (var item in arr.Elements)
        {
            var d = Deref<PdfDictionary>(item);
            if (d.Elements.GetName("/Subtype") == subtype) return d;
        }
        throw new Xunit.Sdk.XunitException($"{subtype} 주석 없음");
    }

    private static T Deref<T>(PdfItem? item) where T : PdfItem =>
        item is PdfReference r ? (T)(PdfItem)r.Value : (T)item!;
}
