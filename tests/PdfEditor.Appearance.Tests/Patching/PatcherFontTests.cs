using System.Collections.Immutable;
using System.Text;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Appearance.Tests.Fonts;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Appearance.Tests.Patching;

public class PatcherFontTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-patchfont-" + Guid.NewGuid().ToString("N"));

    public PatcherFontTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static T Deref<T>(PdfItem? item) where T : PdfItem => (T)((item as PdfReference)?.Value ?? item)!;

    private static AnnotationCreate FreeText(string hex, FontUse use) => new(0, "FreeText", "ft-1", new PdfRectF(10, 20, 190, 80), 4,
        [new AddApFont("F1", use),   // 순서가 뒤바뀌어도 자원이 사라지지 않아야 한다
         new SetAppearanceStream($"BT /F1 24 Tf 6 30 Td <{hex}> Tj ET", new PdfRectF(10, 20, 190, 80))]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddApFont_ProviderEmptyOrMissing_ThrowsInvalidOperation(bool nullProvider)
    {
        string pdf = Path.Combine(_dir, "a.pdf");
        TestPdfFactory.CreateBlank(pdf, 1, 200, 100);
        using var font = AppFont.Load(FontTestSupport.FontPath());
        var (use, hex) = FontTestSupport.UseFor(font, "가");
        using var patcher = new PdfSharpAnnotationPatcher(nullProvider ? () => null! : () => []);
        var plan = new PatchPlan([], [FreeText(hex, use)], []);
        Assert.Throws<InvalidOperationException>(() => patcher.Apply(pdf, null, plan, AnnotationAssets.Empty));
    }

    [Fact]
    public void AddApFont_MergesIntoExistingResources_KeepsExtGState()
    {
        // 입력: PDFium이 만든 것처럼 /Resources /ExtGState /GS 가 든 AP를 가진 주석
        string pdf = Path.Combine(_dir, "b.pdf");
        AnnotatedPdfFactory.CreateMixed(pdf);
        using (var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify))
        {
            var annots = Deref<PdfArray>(doc.Pages[0].Elements["/Annots"]);
            var annot = Deref<PdfDictionary>(annots.Elements[1]);
            var gs = new PdfDictionary(doc);
            gs.Elements["/ca"] = new PdfReal(0.5);
            var egs = new PdfDictionary(doc);
            egs.Elements["/GS"] = gs;
            var res = new PdfDictionary(doc);
            res.Elements["/ExtGState"] = egs;
            var form = new PdfDictionary(doc);
            form.Elements["/Type"] = new PdfName("/XObject");
            form.Elements["/Subtype"] = new PdfName("/Form");
            form.Elements["/BBox"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(10), new PdfReal(10));
            form.Elements["/Resources"] = res;
            form.CreateStream(Encoding.ASCII.GetBytes("/GS gs"));
            doc.Internals.AddObject(form);
            var ap = new PdfDictionary(doc);
            ap.Elements["/N"] = form.Reference!;
            annot.Elements["/AP"] = ap;
            doc.Save(pdf);
        }

        using var font = AppFont.Load(FontTestSupport.FontPath());
        var (use, _) = FontTestSupport.UseFor(font, "가나");
        using var patcher = new PdfSharpAnnotationPatcher(() => font.Data);
        patcher.Apply(pdf, null, new PatchPlan([new AnnotationPatch(0, 1, "", [new AddApFont("F1", use)])], [], []), AnnotationAssets.Empty);

        using var after = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var a = Deref<PdfDictionary>(Deref<PdfArray>(after.Pages[0].Elements["/Annots"]).Elements[1]);
        var f = Deref<PdfDictionary>(Deref<PdfDictionary>(a.Elements["/AP"]).Elements["/N"]);
        var r = Deref<PdfDictionary>(f.Elements["/Resources"]);
        Assert.True(Deref<PdfDictionary>(r.Elements["/ExtGState"]).Elements.ContainsKey("/GS"));
        var type0 = Deref<PdfDictionary>(Deref<PdfDictionary>(r.Elements["/Font"]).Elements["/F1"]);
        Assert.Equal("/Type0", type0.Elements.GetName("/Subtype"));
        Assert.Equal("abc", a.Elements.GetString("/XKeep"));
    }

    [Fact]
    public void SetAppearanceStream_AfterAddApFont_KeepsFontAndExtGState()
    {
        string pdf = Path.Combine(_dir, "c.pdf");
        TestPdfFactory.CreateBlank(pdf, 1, 200, 100);
        using var font = AppFont.Load(FontTestSupport.FontPath());
        var (use, hex) = FontTestSupport.UseFor(font, "가나다");
        using var patcher = new PdfSharpAnnotationPatcher(() => font.Data);
        patcher.Apply(pdf, null, new PatchPlan([], [FreeText(hex, use)], []), AnnotationAssets.Empty);

        using var after = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var a = Deref<PdfDictionary>(Deref<PdfArray>(after.Pages[0].Elements["/Annots"]).Elements[0]);
        var f = Deref<PdfDictionary>(Deref<PdfDictionary>(a.Elements["/AP"]).Elements["/N"]);
        var fonts = Deref<PdfDictionary>(Deref<PdfDictionary>(f.Elements["/Resources"]).Elements["/Font"]);
        Assert.True(fonts.Elements.ContainsKey("/F1"));
    }
}
