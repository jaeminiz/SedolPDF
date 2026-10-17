using System.Text.RegularExpressions;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.Appearance.Tests.Fonts;

public class FontEmbedderTests : IDisposable
{
    private const string Sample = "가나다ABC";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-font-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public FontEmbedderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static T Deref<T>(PdfItem? item) where T : PdfItem => (T)((item as PdfReference)?.Value ?? item)!;

    [Fact]
    public void Subset_KeepsCidsOfFullFont()
    {
        using var font = AppFont.Load(FontTestSupport.FontPath());
        int[] cps = [.. Sample.EnumerateRunes().Select(r => r.Value)];
        byte[] sub = FontSubsetter.Subset(font.Data, cps);
        Assert.True(sub.Length < font.Data.Length / 10);

        int[] subCids = CffCharset.GetCidByGid(sub) ?? throw new Exception("subset is not CID-keyed");
        var present = new HashSet<int>(subCids);
        foreach (int cp in cps)
        {
            int fullCid = font.GetCid(font.GetGlyph(cp)!.Value);
            Assert.Contains(fullCid, present);
        }
        // 서브셋은 요청한 글자(+ .notdef 등 소수)만 가진다
        Assert.True(present.Count <= cps.Length + 3, $"subset has {present.Count} CIDs");
    }

    [Fact]
    public void RequireCidKeyed_NameKeyedFont_Throws()
    {
        // 이름 키 방식(전체·서브셋 모두 charset 없음): 조용히 넘어가지 않고 던진다
        var ex = Assert.Throws<InvalidOperationException>(() => FontEmbedder.RequireCidKeyed(false, null));
        Assert.Contains("Name-keyed", ex.Message);
        // 종류가 다를 때도 던진다
        Assert.Throws<InvalidOperationException>(() => FontEmbedder.RequireCidKeyed(true, null));
        Assert.Throws<InvalidOperationException>(() => FontEmbedder.RequireCidKeyed(false, [0, 1]));
        FontEmbedder.RequireCidKeyed(true, [0, 1]);   // CID 키 방식은 통과
    }

    [Fact]
    public void GetCid_InvalidGid_ThrowsArgumentOutOfRange()
    {
        using var font = AppFont.Load(FontTestSupport.FontPath());
        Assert.Throws<ArgumentOutOfRangeException>(() => font.GetCid(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => font.GetCid(int.MaxValue));
        Assert.True(font.GetCid(font.GetGlyph('가')!.Value) > 0);
    }

    [Fact]
    public void ThrowMessages_AreEnglish()
    {
        Assert.Throws<InvalidOperationException>(() => CffCharset.ExtractCff([0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        foreach (var f in new[] { "FontSubsetter.cs", "CffCharset.cs" })
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "PdfEditor.Appearance", "Fonts"))) dir = dir.Parent;
            if (dir is null) return;
            string src = File.ReadAllText(Path.Combine(dir.FullName, "src", "PdfEditor.Appearance", "Fonts", f));
            foreach (var line in src.Split('\n').Where(l => l.Contains("throw new")))
                Assert.DoesNotMatch("[가-힣]", line);
        }
    }

    [Fact]
    public void Embed_Type0IdentityH_CidFontType0C_ToUnicode_W()
    {
        using var font = AppFont.Load(FontTestSupport.FontPath());
        var (use, _) = FontTestSupport.UseFor(font, Sample);
        using var doc = new PdfSharp.Pdf.PdfDocument();
        doc.AddPage();
        var type0 = Deref<PdfDictionary>(FontEmbedder.Embed(doc, font, use));

        Assert.Equal("/Type0", type0.Elements.GetName("/Subtype"));
        Assert.Equal("/Identity-H", type0.Elements.GetName("/Encoding"));
        Assert.Matches(new Regex(@"^/[A-Z]{6}\+"), type0.Elements.GetName("/BaseFont"));
        var uni = Deref<PdfDictionary>(type0.Elements["/ToUnicode"]);
        string cmap = System.Text.Encoding.ASCII.GetString(uni.Stream.UnfilteredValue);
        int gaCid = font.GetCid(font.GetGlyph('가')!.Value);
        Assert.Contains($"<{gaCid:X4}> <AC00>", cmap);

        var cid = Deref<PdfDictionary>(Deref<PdfArray>(type0.Elements["/DescendantFonts"]).Elements[0]);
        Assert.Equal("/CIDFontType0", cid.Elements.GetName("/Subtype"));
        var w = Deref<PdfArray>(cid.Elements["/W"]);
        Assert.Equal(use.Glyphs.Select(g => g.Cid).Distinct().Count() * 2, w.Elements.Count);
        var fd = Deref<PdfDictionary>(cid.Elements["/FontDescriptor"]);
        var ff = Deref<PdfDictionary>(fd.Elements["/FontFile3"]);
        Assert.Equal("/CIDFontType0C", ff.Elements.GetName("/Subtype"));
        Assert.True(ff.Stream.Value.Length < 20_000, "embedded CFF is a subset");
    }

    [Fact]
    public void Embed_UnknownCid_Throws()
    {
        using var font = AppFont.Load(FontTestSupport.FontPath());
        var (use, _) = FontTestSupport.UseFor(font, "가");
        var bad = use with { Glyphs = [.. use.Glyphs, new GlyphUse(60000, 1000, "x")] };
        using var doc = new PdfSharp.Pdf.PdfDocument();
        doc.AddPage();
        Assert.Throws<InvalidOperationException>(() => FontEmbedder.Embed(doc, font, bad));
    }

    [Fact]
    public async Task Embed_RendersInPdfium()
    {
        string pdf = Path.Combine(_dir, "blank.pdf");
        TestPdfFactory.CreateBlank(pdf, 1, 200, 100);
        using var font = AppFont.Load(FontTestSupport.FontPath());
        var (use, hex) = FontTestSupport.UseFor(font, Sample);
        using var patcher = new PdfSharpAnnotationPatcher(() => font.Data);
        var plan = new PatchPlan([], [new AnnotationCreate(0, "FreeText", "ft-1", new PdfRectF(10, 20, 190, 80), 4,
            [new SetAppearanceStream($"BT 0 0 0 rg /F1 24 Tf 6 30 Td <{hex}> Tj ET", new PdfRectF(10, 20, 190, 80)),
             new AddApFont("F1", use)])], []);
        patcher.Apply(pdf, null, plan, AnnotationAssets.Empty);

        var (withAnn, without) = await _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(_t, pdf);
            using var page = doc.LoadPage(0);
            return (NonWhite(page.RenderTile(2, 0, 0, 400, 200, true)), NonWhite(page.RenderTile(2, 0, 0, 400, 200, false)));
        });
        Assert.Equal(0, without);
        Assert.True(withAnn > 400, $"non-white pixels: {withAnn}");
    }

    private static int NonWhite(byte[] bgra)
    {
        int n = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
            if (bgra[i] < 200 || bgra[i + 1] < 200 || bgra[i + 2] < 200) n++;
        return n;
    }
}
