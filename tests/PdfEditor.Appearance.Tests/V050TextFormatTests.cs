using System.Text;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Appearance.Tests.Fonts;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Appearance.Tests;

/// <summary>0.5.0 글자 서식: Painter 출력(굵게·기울임·밑줄·취소선·명조), 저장 키 왕복, 명조 글꼴 서브셋 임베드, 알 수 없는 키 보존.</summary>
public class V050TextFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-v50tf-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _sans = AppFont.Load(FontTestSupport.FontPath());
    private readonly AppFont _serif;
    private readonly PdfAppearanceSource _ap;
    private readonly PdfSharpAnnotationPatcher _patcher;
    private static readonly PdfColor Ink = new(20, 30, 200);

    public V050TextFormatTests()
    {
        Directory.CreateDirectory(_dir);
        _serif = AppFont.Load(Path.Combine(Path.GetDirectoryName(FontTestSupport.FontPath())!, "NotoSerifKR-Regular.otf"));
        _ap = new PdfAppearanceSource(_sans, null, _serif);
        _patcher = new PdfSharpAnnotationPatcher(_sans) { SerifFont = () => _serif };
    }

    public void Dispose()
    {
        _t.Dispose();
        _patcher.Dispose();
        _sans.Dispose();
        _serif.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);
    private SaveService Svc() => new([], _ap, _patcher);
    private PaintContext Ctx => new(_sans, AnnotationAssets.Empty, _serif);

    private static Annotation Box(Func<Annotation, Annotation>? f = null, FreeTextIntent intent = FreeTextIntent.TextBox)
    {
        var a = new Annotation
        {
            Kind = AnnotKind.FreeText, Intent = intent, Rect = new PdfRectF(20, 100, 220, 160), Contents = "Hello 가나다\n두 번째 줄", FontSizePt = 14f,
            TextColor = Ink, Color = new PdfColor(200, 0, 0), BorderWidth = 1f, Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print, Author = "tester",
            CreatedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), ModifiedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero),
        };
        return f is null ? a : f(a);
    }

    private async Task<string> SaveWithAsync(Annotation a, string name)
    {
        TestPdfFactory.CreateBlank(P(name + "-src.pdf"), 1, 300, 300);
        await using var s = await DocumentSession.OpenAsync(_t, P(name + "-src.pdf"), null);
        await s.AnnotationsReady;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a with { Id = s.Annotations.NewId() }));
        string target = P(name + ".pdf");
        await Svc().SaveAsync(s, target);
        return target;
    }

    private static PdfDictionary FirstAnnot(PdfSharp.Pdf.PdfDocument doc)
    {
        var arr = (PdfArray)((doc.Pages[0].Elements["/Annots"] as PdfReference)?.Value ?? doc.Pages[0].Elements["/Annots"]!);
        return (PdfDictionary)((arr.Elements[0] as PdfReference)?.Value ?? arr.Elements[0]);
    }

    private static string ApContent(PdfDictionary annot)
    {
        var ap = (PdfDictionary)((annot.Elements.GetDictionary("/AP")!.Elements["/N"] as PdfReference)?.Value ?? annot.Elements.GetDictionary("/AP")!.Elements["/N"]!);
        return Encoding.Latin1.GetString(((PdfDictionary.PdfStream)ap.Stream!).UnfilteredValue);
    }

    // ---- Painter / 내용 스트림 ----

    [Fact]
    public void Plain_Unchanged_NoTrNoSkew()
    {
        string c = PdfContentWriter.Write(AnnotationPainter.Paint(Box(), 0, Ctx));
        Assert.DoesNotContain(" Tr\n", c);
        Assert.Contains("1 0 0 1 ", c);
        var d = AnnotationPainter.Paint(Box(), 0, Ctx);
        Assert.Equal("", d.Texts[0].Family);
        Assert.False(d.Texts[0].Bold);
        Assert.False(d.Texts[0].Italic);
    }

    [Fact]
    public void Bold_UsesFillStrokeRenderMode_WidthProportionalToSize()
    {
        string c = PdfContentWriter.Write(AnnotationPainter.Paint(Box(a => a with { Bold = true }), 0, Ctx));
        Assert.Contains("2 Tr\n", c);
        Assert.Contains("0.42 w\n", c);                 // 14pt x 0.03
        Assert.Contains("0.078 0.118 0.784 RG\n", c);   // 선 색 = 글자 색
        Assert.Contains("0 Tr\n", c);
        string big = PdfContentWriter.Write(AnnotationPainter.Paint(Box(a => a with { Bold = true, FontSizePt = 28f }), 0, Ctx));
        Assert.Contains("0.84 w\n", big);
    }

    [Fact]
    public void Italic_ShearsEachGlyphOrigin_WithSharedSkew()
    {
        string c = PdfContentWriter.Write(AnnotationPainter.Paint(Box(a => a with { Italic = true }), 0, Ctx));
        Assert.Contains($"1 0 {AppFontCatalog.SyntheticItalicSkew.ToString(System.Globalization.CultureInfo.InvariantCulture)} 1 ", c);
        Assert.DoesNotContain("1 0 0 1 ", c.Split("BT\n")[1]);
    }

    [Fact]
    public void UnderlineAndStrike_OneLinePerTextLine_InTextColor_WithinGlyphExtent()
    {
        var plain = AnnotationPainter.Paint(Box(), 0, Ctx);
        var d = AnnotationPainter.Paint(Box(a => a with { Underline = true, StrikeThrough = true }), 0, Ctx);
        Assert.Equal(plain.Ops.Length + 4, d.Ops.Length);   // 두 줄 x (밑줄 + 취소선)
        var added = d.Ops.Skip(plain.Ops.Length).ToArray();
        Assert.All(added, op => { Assert.Equal(Ink, op.Stroke); Assert.Null(op.Fill); Assert.Equal(2, op.Path.Length); });
        var line1 = d.Texts[0].Glyphs.Where(g => Math.Abs(g.Origin.Y - d.Texts[0].Glyphs[0].Origin.Y) < 0.01f).ToArray();
        var ul = (MoveTo)added[0].Path[0];
        var ulEnd = (LineTo)added[0].Path[1];
        Assert.Equal(line1[0].Origin.X, ul.P.X, 2);
        Assert.Equal(line1[^1].Origin.X + line1[^1].Advance, ulEnd.P.X, 2);
        float baseline = line1[0].Origin.Y;
        Assert.True(((MoveTo)added[0].Path[0]).P.Y < baseline);   // 밑줄은 기준선 아래
        Assert.True(((MoveTo)added[1].Path[0]).P.Y > baseline);   // 취소선은 기준선 위
        Assert.Equal(14f * FreeTextPainter.UnderlineThicknessRatio, added[0].StrokeWidth, 3);
    }

    [Fact]
    public void Serif_UsesSerifFont_ForLayoutAndFontUse_AndFallsBackWithoutIt()
    {
        var sans = AnnotationPainter.Paint(Box(), 0, Ctx);
        var serif = AnnotationPainter.Paint(Box(a => a with { TextFamily = TextFontFamily.Serif }), 0, Ctx);
        Assert.Equal(FontUse.SerifFamily, serif.Texts[0].Family);
        Assert.Equal(FontUse.SerifFamily, serif.CollectFontUse().Family);
        Assert.Equal("", sans.CollectFontUse().Family);
        // 글꼴이 다르면 글리프 번호/폭이 다르다
        Assert.NotEqual(sans.Texts[0].Glyphs.Select(g => g.Advance).Sum(), serif.Texts[0].Glyphs.Select(g => g.Advance).Sum());
        var fb = AnnotationPainter.Paint(Box(a => a with { TextFamily = TextFontFamily.Serif }), 0, new PaintContext(_sans, AnnotationAssets.Empty));
        Assert.Equal("", fb.Texts[0].Family);
        Assert.Equal(sans.Texts[0].Glyphs.Select(g => g.Advance).Sum(), fb.Texts[0].Glyphs.Select(g => g.Advance).Sum(), 3);
    }

    [Fact]
    public void Typewriter_Italic_WidensBox_BoldDoesNot_FamilyChangesWidth()
    {
        var tw = Box(a => a with { Contents = "Typewriter 한글 AVAT" }, FreeTextIntent.Typewriter);
        var plain = AnnotationPainter.FitTypewriter(tw, Ctx);
        var ital = AnnotationPainter.FitTypewriter(tw with { Italic = true }, Ctx);
        var bold = AnnotationPainter.FitTypewriter(tw with { Bold = true }, Ctx);
        var serif = AnnotationPainter.FitTypewriter(tw with { TextFamily = TextFontFamily.Serif }, Ctx);
        Assert.True(ital.Rect.Width > plain.Rect.Width);
        Assert.Equal(plain.Rect.Width, bold.Rect.Width, 3);
        Assert.NotEqual(plain.Rect.Width, serif.Rect.Width, 2);
        Assert.Equal(plain.Rect.Left, ital.Rect.Left, 3);
        Assert.Equal(plain.Rect.Top, ital.Rect.Top, 3);
    }

    [Fact]
    public void CalloutAndTextBox_BothFormatted_ByTheSameCode()
    {
        var co = Box(a => a with
        {
            Intent = FreeTextIntent.Callout, TextBox = new PdfRectF(100, 100, 220, 160), Vertices = [new PdfPointF(40, 60), new PdfPointF(100, 120)],
            EndEnding = LineEnding.OpenArrow, Bold = true, Underline = true,
        });
        var d = AnnotationPainter.Paint(co, 0, Ctx);
        Assert.True(d.Texts[0].Bold);
        Assert.Contains(d.Ops, op => Math.Abs(op.StrokeWidth - 14f * FreeTextPainter.UnderlineThicknessRatio) < 0.001f);
    }

    // ---- 저장 왕복 ----

    [Theory]
    [InlineData(false, false, false, false, TextFontFamily.Sans)]
    [InlineData(true, false, false, false, TextFontFamily.Sans)]
    [InlineData(false, true, false, false, TextFontFamily.Serif)]
    [InlineData(true, true, true, true, TextFontFamily.Serif)]
    [InlineData(false, false, true, true, TextFontFamily.Sans)]
    public async Task Format_RoundTrips_AndWritesNonstandardKeysOnlyWhenNeeded(bool bold, bool italic, bool underline, bool strike, TextFontFamily family)
    {
        var a = Box(x => x with { Bold = bold, Italic = italic, Underline = underline, StrikeThrough = strike, TextFamily = family });
        string target = await SaveWithAsync(a, $"rt-{bold}-{italic}-{underline}-{strike}-{family}");
        var o = Assert.Single(AnnotationReader.Read(File.ReadAllBytes(target), null, [0])[0]);
        Assert.Equal(ReadOnlyReason.None, o.Reason);
        Assert.Equal(bold, o.Data!.Bold);
        Assert.Equal(italic, o.Data.Italic);
        Assert.Equal(underline, o.Data.Underline);
        Assert.Equal(strike, o.Data.StrikeThrough);
        Assert.Equal(family, o.Data.TextFamily);
        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var d = FirstAnnot(doc);
        Assert.Equal(family == TextFontFamily.Serif, d.Elements.ContainsKey("/SedolFont"));
        Assert.Equal(bold || italic || underline || strike, d.Elements.ContainsKey("/SedolFmt"));
        Assert.StartsWith("0.078", d.Elements.GetString("/DA").Contains("rg") ? "0.078" : "x");   // /DA는 그대로(글자 색 rg …)
    }

    [Fact]
    public async Task Serif_EmbedsSerifSubset_InAp()
    {
        string target = await SaveWithAsync(Box(x => x with { TextFamily = TextFontFamily.Serif }), "serif-embed");
        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var annot = FirstAnnot(doc);
        var ap = (PdfDictionary)((annot.Elements.GetDictionary("/AP")!.Elements["/N"] as PdfReference)?.Value ?? annot.Elements.GetDictionary("/AP")!.Elements["/N"]!);
        var fonts = ap.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/Font")!;
        var f1 = (PdfDictionary)((fonts.Elements["/F1"] as PdfReference)?.Value ?? fonts.Elements["/F1"]!);
        Assert.Contains("NotoSerifKR", f1.Elements.GetName("/BaseFont"));
        string sansTarget = await SaveWithAsync(Box(), "sans-embed");
        using var doc2 = PdfReader.Open(sansTarget, PdfDocumentOpenMode.Import);
        var ap2 = (PdfDictionary)((FirstAnnot(doc2).Elements.GetDictionary("/AP")!.Elements["/N"] as PdfReference)?.Value ?? FirstAnnot(doc2).Elements.GetDictionary("/AP")!.Elements["/N"]!);
        var f2 = (PdfDictionary)((ap2.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/Font")!.Elements["/F1"] as PdfReference)!.Value);
        Assert.Contains("NotoSansKR", f2.Elements.GetName("/BaseFont"));
    }

    [Fact]
    public async Task Edit_ChangeFormatThenBack_RemovesKeys_AndKeepsUnknownKeys()
    {
        string first = await SaveWithAsync(Box(x => x with { Bold = true, TextFamily = TextFontFamily.Serif }), "ed1");
        using (var doc = PdfReader.Open(first, PdfDocumentOpenMode.Modify))
        {
            FirstAnnot(doc).Elements["/FooBar"] = new PdfString("keep-me");
            doc.Save(P("ed1b.pdf"));
        }
        await using (var s = await DocumentSession.OpenAsync(_t, P("ed1b.pdf"), null))
        {
            await s.AnnotationsReady;
            var id = s.Pages[0].Id;
            var before = Assert.Single(s.Annotations.Get(id).Items).Data;
            Assert.True(before.Bold);
            Assert.Equal(TextFontFamily.Serif, before.TextFamily);
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(before, before with { Bold = false, Italic = true, TextFamily = TextFontFamily.Sans })]));
            await Svc().SaveAsync(s, P("ed2.pdf"));
        }
        using (var doc = PdfReader.Open(P("ed2.pdf"), PdfDocumentOpenMode.Import))
        {
            var d = FirstAnnot(doc);
            Assert.Equal("keep-me", d.Elements.GetString("/FooBar"));
            Assert.False(d.Elements.ContainsKey("/SedolFont"));
            Assert.Equal(TextFormat.ItalicBit, d.Elements.GetInteger("/SedolFmt"));
            Assert.Contains("1 0 0.21 1 ", ApContent(d));
        }
        await using (var s = await DocumentSession.OpenAsync(_t, P("ed2.pdf"), null))
        {
            await s.AnnotationsReady;
            var id = s.Pages[0].Id;
            var before = Assert.Single(s.Annotations.Get(id).Items).Data;
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(before, before with { Italic = false })]));
            await Svc().SaveAsync(s, P("ed3.pdf"));
        }
        using var doc3 = PdfReader.Open(P("ed3.pdf"), PdfDocumentOpenMode.Import);
        Assert.False(FirstAnnot(doc3).Elements.ContainsKey("/SedolFmt"));
        Assert.DoesNotContain(" Tr\n", ApContent(FirstAnnot(doc3)));
    }

    [Fact]
    public async Task Rendered_BoldHasMoreInk_ThanRegular_AndOutsideRectIsClean()
    {
        const int W = 300, H = 300;
        async Task<byte[]> Render(string pdf) => await _t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
            using var pg = d.LoadPage(0);
            return pg.RenderTile(1, 0, 0, W, H, withAnnotations: true);
        });
        static int InkPx(byte[] b) { int n = 0; for (int i = 0; i < b.Length; i += 4) if (b[i + 2] < 140 && b[i + 1] < 140) n++; return n; }
        var plain = await Render(await SaveWithAsync(Box(x => x with { Color = null, BorderWidth = 0 }), "r-plain"));
        var bold = await Render(await SaveWithAsync(Box(x => x with { Color = null, BorderWidth = 0, Bold = true }), "r-bold"));
        Assert.True(InkPx(bold) > InkPx(plain), $"bold {InkPx(bold)} vs plain {InkPx(plain)}");
    }

    [Fact]
    public void DefaultFormat_NeedsNoKeys_TextFormatHelpers()
    {
        Assert.False(TextFormat.NeedsKeys(Box()));
        Assert.True(TextFormat.NeedsKeys(Box(a => a with { Underline = true })));
        Assert.Equal(15, TextFormat.Sanitize(0xFF));
        Assert.Equal(TextFontFamily.Serif, TextFormat.FamilyFromName("/Serif"));
        Assert.Equal(TextFontFamily.Sans, TextFormat.FamilyFromName("Comic"));
        Assert.Equal(TextFontFamily.Sans, TextFormat.FamilyFromName(null));
    }
}
