using System.Text;
using Pdf.Native;
using PdfEditor.Appearance;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Saving;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests.Annotations;

/// <summary>
/// 0.13.0 글자별 글꼴 대체: 고른 글꼴이 가진 글자는 그 글꼴, 없는 글자만 Noto. 폭 재기·줄바꿈·그리기·저장이 같은 구간 분할을 쓴다.
/// arial.ttf가 없는 PC에서는 아무것도 하지 않고 통과한다.
/// </summary>
public class GlyphFallbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-fallback-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _noto = AppFont.Load(NotoPath());
    private static readonly string WinFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    private static string ArialPath => Path.Combine(WinFonts, "arial.ttf");

    public GlyphFallbackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        _noto.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string NotoPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) return Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf");
        throw new FileNotFoundException("assets/fonts/NotoSansKR-Regular.otf");
    }

    private static SystemFontLibrary ArialLibrary()
        => new(() => SystemFontLibrary.Scan([WinFonts]).Where(f => Path.GetFileName(f.Path).Equals("arial.ttf", StringComparison.OrdinalIgnoreCase)));

    private static Annotation Box(string text) => new() { Kind = AnnotKind.FreeText, FontName = "Arial", Contents = text, FontSizePt = 12, Rect = new PdfRectF(10, 100, 300, 140) };

    [Fact]
    public void Layout_MixedText_SplitsByCoverage_AndNeutralsFollowPrevious()
    {
        if (!File.Exists(ArialPath)) return;
        using var arial = AppFont.Load(ArialPath);
        var laid = TextLayout.Layout([arial, _noto], "Tank 탱크 A-1", 12, float.PositiveInfinity, TextAlign.Left, new PdfPointF(0, 100));
        Assert.Empty(laid.MissingCodepoints);
        var g = laid.Lines[0];
        // 글자 → 글꼴 번호 (Text가 비어 있지 않은 글리프)
        string Fonts(int idx) => string.Concat(g.Where(x => x.FontIndex == idx).Select(x => x.Text));
        Assert.Equal("Tank A-1", Fonts(0).Replace("  ", " ").Trim());   // 영문·숫자·기호는 Arial (가운데 공백은 어느 쪽이든)
        Assert.Contains("탱", Fonts(1));
        Assert.Contains("크", Fonts(1));
        Assert.DoesNotContain("T", Fonts(1));
        Assert.DoesNotContain("A", Fonts(1));
        Assert.Equal(1, g.First(x => x.Text == "탱").FontIndex);
        Assert.Equal(0, g.First(x => x.Text == "-").FontIndex);
        // 구간이 두세 개뿐: 글꼴이 바뀌는 곳은 Tank→탱, 크→A
        int changes = 0;
        for (int i = 1; i < g.Length; i++) if (g[i].FontIndex != g[i - 1].FontIndex) changes++;
        Assert.Equal(2, changes);
        // 글리프는 위치 순서대로 겹치지 않는다
        for (int i = 1; i < g.Length; i++) Assert.True(g[i].Origin.X >= g[i - 1].Origin.X);
    }

    [Fact]
    public void Layout_SingleFont_IsSameAsBefore()
    {
        var a = TextLayout.Layout(_noto, "한글 text, 줄바꿈 시험 가나다라마바사", 12, 60, TextAlign.Left, new PdfPointF(0, 100));
        var b = TextLayout.Layout([_noto], "한글 text, 줄바꿈 시험 가나다라마바사", 12, 60, TextAlign.Left, new PdfPointF(0, 100));
        Assert.Equal(a.Lines.Length, b.Lines.Length);
        for (int i = 0; i < a.Lines.Length; i++)
            Assert.Equal(a.Lines[i].Select(x => (x.Gid, x.Origin)), b.Lines[i].Select(x => (x.Gid, x.Origin)));
        Assert.All(a.Lines.SelectMany(l => l), x => Assert.Equal(0, x.FontIndex));
    }

    [Fact]
    public void Layout_WrapsOnMixedWidths()
    {
        if (!File.Exists(ArialPath)) return;
        using var arial = AppFont.Load(ArialPath);
        var narrow = TextLayout.Layout([arial, _noto], "Tank 탱크 A-1 Tank 탱크 A-1", 12, 70, TextAlign.Left, new PdfPointF(0, 100));
        Assert.True(narrow.Lines.Length > 1);
        // 줄마다 폭이 상자 안(끝 공백 제외)
        foreach (var line in narrow.Lines)
            Assert.True(line[^1].Origin.X + line[^1].Advance <= 70.01f, "line wider than box");
    }

    [Fact]
    public void Resolve_Mixed_HasFallback_LatinOnlyAndKoreanOnlyDoNot()
    {
        if (!File.Exists(ArialPath)) return;
        var ctx = new PaintContext(_noto, AnnotationAssets.Empty) { SystemFonts = ArialLibrary() };
        var mixed = ctx.Resolve(Box("Tank 탱크 A-1"))!;
        Assert.StartsWith(SystemFontFace.KeyPrefix, mixed.Key);
        Assert.NotNull(mixed.Fallback);
        Assert.Equal("", mixed.Fallback!.Key);
        Assert.False(mixed.Substituted);

        var latin = ctx.Resolve(Box("Tank A-1"))!;
        Assert.StartsWith(SystemFontFace.KeyPrefix, latin.Key);
        Assert.Null(latin.Fallback);

        var korean = ctx.Resolve(Box("탱크 시험"))!;   // 지금과 같다: 전부 Noto
        Assert.Same(_noto, korean.Font);
        Assert.Equal("", korean.Key);
        Assert.Null(korean.Fallback);
        Assert.True(korean.Substituted);
    }

    [Fact]
    public void Paint_MixedText_MakesRunsPerFont()
    {
        if (!File.Exists(ArialPath)) return;
        var ctx = new PaintContext(_noto, AnnotationAssets.Empty) { SystemFonts = ArialLibrary() };
        var d = AnnotationPainter.Paint(Box("Tank 탱크 A-1"), 0, ctx);
        Assert.Equal(3, d.Texts.Length);
        Assert.StartsWith(SystemFontFace.KeyPrefix, d.Texts[0].Family);
        Assert.Equal("", d.Texts[1].Family);
        Assert.Equal(d.Texts[0].Family, d.Texts[2].Family);
        Assert.NotEqual(d.Texts[0].FontResource, d.Texts[1].FontResource);
        Assert.Equal(d.Texts[0].FontResource, d.Texts[2].FontResource);
        var uses = d.CollectFontUses();
        Assert.Equal(2, uses.Count);
        // 대체 글꼴의 가짜 굵게는 Noto 쪽 런에만 붙는다
        var bold = AnnotationPainter.Paint(Box("Tank 탱크") with { Bold = true }, 0, ctx);
        Assert.True(bold.Texts.Where(t => t.Family == "").All(t => t.Bold));
    }

    [Fact]
    public void Paint_LatinOnly_StaysSingleRunWithF1()
    {
        if (!File.Exists(ArialPath)) return;
        var ctx = new PaintContext(_noto, AnnotationAssets.Empty) { SystemFonts = ArialLibrary() };
        var d = AnnotationPainter.Paint(Box("Tank A-1"), 0, ctx);
        var run = Assert.Single(d.Texts);
        Assert.Equal("F1", run.FontResource);
        Assert.Single(d.CollectFontUses());
    }

    /// <summary>저장하면 Arial 서브셋과 Noto 서브셋이 둘 다 들어가고, PDFium이 글자를 같게 읽는다.</summary>
    [Fact]
    public async Task Save_MixedText_EmbedsBothFonts_AndTextIsSame()
    {
        if (!File.Exists(ArialPath)) return;
        const string text = "Tank 탱크 A-1";
        var lib = ArialLibrary();
        var ap = PdfAppearanceSource.WithFontProvider(() => _noto, null, null, lib);
        using var patcher = PdfSharpAnnotationPatcher.Shared(() => _noto, null, lib);
        string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "freetext-rc-only.pdf");
        string saved = Path.Combine(_dir, "mixed.pdf");
        await using (var s = await DocumentSession.OpenAsync(_t, fixture, null))
        {
            await s.AnnotationsReady;
            var a = s.Annotations.Get(s.Pages[0].Id).Items.Single().Data;
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(s.Pages[0].Id, [(a, a with { Contents = text, FontName = "Arial", Bold = false, Italic = false })]));
            await new SaveService([], ap, patcher).SaveAsync(s, saved);
        }

        var back = AnnotationReader.Read(File.ReadAllBytes(saved), null, [0])[0].Single(o => o.Data is not null).Data!;
        Assert.Equal("Arial", back.FontName);   // 이름은 고른 그대로
        Assert.Equal(text, back.Contents);

        string moved = Path.Combine(_dir, "mixed-moved.pdf");
        using (var doc = PdfReader.Open(saved, PdfDocumentOpenMode.Modify))
        {
            var baseFonts = doc.Internals.GetAllObjects().OfType<PdfDictionary>()
                .Where(d => d.Elements.GetName("/Type") == "/Font" && d.Elements.GetName("/Subtype") == "/Type0")
                .Select(d => d.Elements.GetName("/BaseFont")).ToList();
            Assert.Contains(baseFonts, b => b.Contains("Arial", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(baseFonts, b => b.Contains("NotoSansKR", StringComparison.Ordinal));

            var page = doc.Pages[0];
            var annots = (PdfArray)Deref(page.Elements["/Annots"]);
            var annot = (PdfDictionary)Deref(annots.Elements[0]);
            var form = (PdfDictionary)Deref(((PdfDictionary)Deref(annot.Elements["/AP"])).Elements["/N"]);
            var xo = new PdfDictionary(doc);
            xo.Elements["/Fm0"] = form.Reference!;
            var res = new PdfDictionary(doc);
            res.Elements["/XObject"] = xo;
            page.Elements["/Resources"] = res;
            var cont = new PdfDictionary(doc);
            cont.CreateStream(Encoding.ASCII.GetBytes("/Fm0 Do"));
            doc.Internals.AddObject(cont);
            page.Elements["/Contents"] = cont.Reference!;
            page.Elements.Remove("/Annots");
            doc.Save(moved);
        }
        string got = await _t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(_t, moved);
            using var p = d.LoadPage(0);
            using var tp = p.LoadText();
            return tp.GetText(0, tp.CharCount);
        });
        Assert.Equal(text, got.Trim());
    }

    private static PdfItem Deref(PdfItem? i) => i is PdfReference r ? r.Value : i!;
}
