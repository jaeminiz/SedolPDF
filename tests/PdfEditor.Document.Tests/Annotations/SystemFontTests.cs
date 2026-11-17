using System.Collections.Immutable;
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
/// 0.12.0 시스템 글꼴(docs/design/0.12.0-system-fonts.md): 글꼴 머리 읽기, 이름 찾기, 포함 금지 거르기, 대체, TrueType 서브셋 저장.
/// 맑은 고딕·바탕이 필요한 시험은 그 글꼴이 없는 PC에서 아무것도 하지 않고 통과한다.
/// </summary>
public class SystemFontTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-sysfont-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _font = AppFont.Load(NotoPath());

    private static readonly string WinFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    private static string Malgun => Path.Combine(WinFonts, "malgun.ttf");
    private static string MalgunBold => Path.Combine(WinFonts, "malgunbd.ttf");
    private static string Batang => Path.Combine(WinFonts, "batang.ttc");

    public SystemFontTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        _font.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string NotoPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) return Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf");
        throw new FileNotFoundException("assets/fonts/NotoSansKR-Regular.otf");
    }

    private static SystemFontLibrary MalgunLibrary()
        => new(() => SystemFontLibrary.Scan([WinFonts]).Where(f => f.Path.Contains("malgun", StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void BundledNoto_IsCidCffAndUsable()
    {
        var f = Assert.Single(OpenTypeInfo.ReadFaces(NotoPath()));
        Assert.Equal("Noto Sans KR", f.Family);
        Assert.Equal(FontOutline.CffCid, f.Outline);
        Assert.True(f.Usable);
        Assert.Equal(FontOutline.CffCid, _font.Outline);
    }

    [Fact]
    public void Malgun_ReadsKoreanAndEnglishNames()
    {
        if (!File.Exists(Malgun)) return;
        var f = Assert.Single(OpenTypeInfo.ReadFaces(Malgun));
        Assert.Equal("Malgun Gothic", f.Family);
        Assert.Equal("맑은 고딕", f.LocalFamily);
        Assert.Equal("맑은 고딕", f.DisplayFamily);
        Assert.Equal(FontOutline.TrueType, f.Outline);
        Assert.False(f.Serif);
        Assert.False(f.IsBold);
        Assert.True(f.Usable);
    }

    [Fact]
    public void Collection_ReadsEveryFace()
    {
        if (!File.Exists(Batang)) return;
        var faces = OpenTypeInfo.ReadFaces(Batang);
        Assert.True(faces.Count >= 2);
        Assert.Equal(Enumerable.Range(0, faces.Count), faces.Select(f => f.FaceIndex));
        Assert.Contains(faces, f => f.LocalFamily == "바탕");
        Assert.Contains(faces, f => f.Serif);
    }

    [Theory]
    [InlineData("맑은 고딕")]
    [InlineData("Malgun Gothic")]
    [InlineData("malgungothic")]
    [InlineData("MalgunGothic")]
    public void Find_ByAnyName(string name)
    {
        if (!File.Exists(Malgun)) return;
        var face = MalgunLibrary().Find(name, bold: false, italic: false);
        Assert.NotNull(face);
        Assert.Equal("Malgun Gothic", face!.Info.Family);
        Assert.False(face.Info.IsBold);
    }

    [Fact]
    public void Find_Bold_PicksRealBoldFace()
    {
        if (!File.Exists(MalgunBold)) return;
        var face = MalgunLibrary().Find("맑은 고딕", bold: true, italic: false);
        Assert.NotNull(face);
        Assert.True(face!.Info.IsBold);
        Assert.EndsWith("malgunbd.ttf", face.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotEmbeddable_IsHiddenAndNotFound()
    {
        var restricted = new OpenTypeFace(0, "Locked Font", "잠긴 글꼴", "LockedFont", ["Locked Font", "잠긴 글꼴"], 400, false, false, 0x0002, FontOutline.TrueType, false);
        var variable = restricted with { Family = "Var Font", LocalFamily = null, Names = ["Var Font"], FsType = 0, Variable = true };
        var lib = new SystemFontLibrary(() => [new SystemFontFace("x:/locked.ttf", restricted), new SystemFontFace("x:/var.ttf", variable)]);
        Assert.Empty(lib.Families);
        Assert.Null(lib.Find("잠긴 글꼴", false, false));
        Assert.Null(lib.Find("Var Font", false, false));
    }

    [Fact]
    public void Resolve_MissingFont_FallsBackToNoto_AndKeepsName()
    {
        var ctx = new PaintContext(_font, AnnotationAssets.Empty) { SystemFonts = new SystemFontLibrary(() => []) };
        var a = new Annotation { Kind = AnnotKind.FreeText, FontName = "없는 글꼴", Bold = true };
        var r = ctx.Resolve(a)!;
        Assert.Same(_font, r.Font);
        Assert.Equal("", r.Key);
        Assert.True(r.Substituted);
        Assert.True(r.SyntheticBold);
        Assert.Contains("'없는 글꼴'", FreeTextRichText.Ds(a));   // 그릴 때 대체해도 이름은 파일에 남는다
    }

    [Fact]
    public void Resolve_RealBoldFace_TurnsOffSyntheticBold()
    {
        if (!File.Exists(MalgunBold)) return;
        var ctx = new PaintContext(_font, AnnotationAssets.Empty) { SystemFonts = MalgunLibrary() };
        var r = ctx.Resolve(new Annotation { Kind = AnnotKind.FreeText, FontName = "맑은 고딕", Bold = true })!;
        Assert.StartsWith(SystemFontFace.KeyPrefix, r.Key);
        Assert.False(r.SyntheticBold);
        Assert.False(r.Substituted);
    }

    [Fact]
    public void ChangingOnlyFontName_IsAChange()
    {
        var a = new Annotation { Kind = AnnotKind.FreeText, FontName = "맑은 고딕" };
        Assert.False(AnnotationComparer.Equivalent(a, a with { FontName = "굴림" }));
        Assert.False(AnnotationComparer.Equivalent(a, a with { FontName = null }));
    }

    [Theory]
    [InlineData("Sans", "Sans", null)]
    [InlineData("Serif:바탕", "Serif", "바탕")]
    [InlineData("Sans:맑은 고딕", "Sans", "맑은 고딕")]
    public void StyleFontSetting_RoundTrips(string value, string family, string? name)
    {
        Assert.Equal((family, name), TextFormat.SplitFontSetting(value));
        var a = new Annotation { Kind = AnnotKind.FreeText, TextFamily = TextFormat.FamilyFromName(family), FontName = name };
        Assert.Equal(value, TextFormat.FontSetting(a));
    }

    /// <summary>맑은 고딕으로 바꾼 글 상자를 저장하면 TrueType 서브셋(/CIDFontType2, /FontFile2)이 들어가고, 서브셋의 글리프 번호가 전체 글꼴과 같다.</summary>
    [Fact]
    public async Task Save_WithMalgun_EmbedsTrueTypeSubset()
    {
        if (!File.Exists(Malgun)) return;
        var lib = MalgunLibrary();
        var ap = PdfAppearanceSource.WithFontProvider(() => _font, null, null, lib);
        using var patcher = PdfSharpAnnotationPatcher.Shared(() => _font, null, lib);
        string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "freetext-rc-only.pdf");
        string target = Path.Combine(_dir, "malgun.pdf");
        await using (var s = await DocumentSession.OpenAsync(_t, fixture, null))
        {
            await s.AnnotationsReady;
            var a = s.Annotations.Get(s.Pages[0].Id).Items.Single().Data;
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(s.Pages[0].Id, [(a, a with { Contents = "가나다 ABC", Bold = false })]));
            await new SaveService([], ap, patcher).SaveAsync(s, target);
        }

        var back = AnnotationReader.Read(File.ReadAllBytes(target), null, [0])[0].Single(o => o.Data is not null).Data!;
        Assert.Equal("맑은 고딕", back.FontName);

        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var type0 = FindFonts(doc).Single(f => f.Elements.GetName("/BaseFont").Contains("MalgunGothic", StringComparison.Ordinal));
        var cid = (PdfDictionary)((PdfReference)((PdfArray)type0.Elements["/DescendantFonts"]!).Elements[0]).Value;
        Assert.Equal("/CIDFontType2", cid.Elements.GetName("/Subtype"));
        Assert.Equal("/Identity", cid.Elements.GetName("/CIDToGIDMap"));
        var desc = (PdfDictionary)((PdfReference)cid.Elements["/FontDescriptor"]!).Value;
        var file2 = (PdfDictionary)((PdfReference)desc.Elements["/FontFile2"]!).Value;
        byte[] sub = file2.Stream!.UnfilteredValue;

        using var full = AppFont.Load(Malgun);
        using var subFont = AppFont.FromBytes(sub);
        foreach (char c in "가나다ABC")
            Assert.Equal(full.GetGlyph(c), subFont.GetGlyph(c));   // 글리프 번호 유지 서브셋
    }

    [Theory]
    [InlineData("/ABCDEF+MalgunGothic", "MalgunGothic")]
    [InlineData("/Helvetica", "Helvetica")]
    [InlineData("/ABCDEF+NotoSansKR-Regular", null)]   // 이 앱이 넣는 글꼴은 원래 글꼴이 아니다
    [InlineData("/NotoSerifKR-Regular", null)]
    [InlineData(null, null)]
    public void CleanBaseFont_StripsSubsetTag(string? baseFont, string? expected)
        => Assert.Equal(expected, AnnotationReader.CleanBaseFont(baseFont));

    [Fact]
    public void CleanBaseFont_DecodesCp949KoreanName()
    {
        var cp949 = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(949)!;
        string escaped = "/" + string.Concat(cp949.GetBytes("맑은 고딕").Select(b => b < 0x80 && b != 0x20 ? ((char)b).ToString() : $"#{b:X2}"));
        Assert.Equal("맑은 고딕", AnnotationReader.CleanBaseFont(escaped));
    }

    /// <summary>/DS·/RC 없이 /DA와 AP 글꼴만 있는 상자(다른 편집기에서 흔하다)도 원래 글꼴 이름을 읽는다.</summary>
    [Fact]
    public void Read_FontNameFromApResources_WhenNoDs()
    {
        var bytes = FreeTextWithApFont("/ABCDEF+MalgunGothic");
        var a = AnnotationReader.Read(bytes, null, [0])[0].Single(o => o.Data is not null).Data!;
        Assert.Equal("MalgunGothic", a.FontName);
        if (File.Exists(Malgun)) Assert.Equal("Malgun Gothic", MalgunLibrary().Find(a.FontName, false, false)!.Info.Family);
    }

    [Theory]
    [InlineData("Helvetica", "Arial", false)]
    [InlineData("Helvetica-Bold", "Arial", true)]
    [InlineData("Arial,Bold", "Arial", true)]
    [InlineData("Times-Roman", "Times New Roman", false)]
    [InlineData("Courier", "Courier New", false)]
    public void Find_StandardFontAliases(string name, string family, bool bold)
    {
        if (!File.Exists(Path.Combine(WinFonts, "arial.ttf")) || !File.Exists(Path.Combine(WinFonts, "times.ttf")) || !File.Exists(Path.Combine(WinFonts, "cour.ttf"))) return;
        var lib = new SystemFontLibrary(() => SystemFontLibrary.Scan([WinFonts]).Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f.Path), "^(arial|times|cour)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)));
        var face = lib.Find(name, false, false);
        Assert.NotNull(face);
        Assert.Equal(family, face!.Info.Family);
        Assert.Equal(bold, face.Info.IsBold);
        Assert.Equal(family, lib.FindFamily(name)!.Name);
    }

    [Fact]
    public void Find_PostScriptStyleName_PicksBoldFace()
    {
        if (!File.Exists(MalgunBold)) return;
        var face = MalgunLibrary().Find("MalgunGothic-Bold", false, false);
        Assert.NotNull(face);
        Assert.True(face!.Info.IsBold);
    }

    /// <summary>고른 글꼴에 없는 글자가 있으면(영문 글꼴에 한글) 글자가 빠지지 않게 Noto로 그린다.</summary>
    [Fact]
    public void Resolve_FontMissingGlyphs_FallsBackToNoto()
    {
        string arial = Path.Combine(WinFonts, "arial.ttf");
        if (!File.Exists(arial)) return;
        var lib = new SystemFontLibrary(() => SystemFontLibrary.Scan([WinFonts]).Where(f => Path.GetFileName(f.Path).Equals("arial.ttf", StringComparison.OrdinalIgnoreCase)));
        var ctx = new PaintContext(_font, AnnotationAssets.Empty) { SystemFonts = lib };
        var latin = ctx.Resolve(new Annotation { Kind = AnnotKind.FreeText, FontName = "Arial", Contents = "Hello" })!;
        Assert.StartsWith(SystemFontFace.KeyPrefix, latin.Key);
        var korean = ctx.Resolve(new Annotation { Kind = AnnotKind.FreeText, FontName = "Arial", Contents = "Hello 안녕" })!;
        Assert.Same(_font, korean.Font);
        Assert.True(korean.Substituted);
    }

    private static byte[] FreeTextWithApFont(string baseFont)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        var font = new PdfDictionary(doc);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/TrueType");
        font.Elements.SetName("/BaseFont", baseFont);
        doc.Internals.AddObject(font);
        var fonts = new PdfDictionary(doc);
        fonts.Elements["/F1"] = font.Reference;
        var res = new PdfDictionary(doc);
        res.Elements["/Font"] = fonts;
        var n = new PdfDictionary(doc);
        n.Elements.SetName("/Type", "/XObject");
        n.Elements.SetName("/Subtype", "/Form");
        n.Elements["/BBox"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(200), new PdfReal(40));
        n.Elements["/Resources"] = res;
        n.CreateStream(System.Text.Encoding.ASCII.GetBytes("BT /F1 12 Tf 2 20 Td (Hello) Tj ET"));
        doc.Internals.AddObject(n);
        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = n.Reference;
        var annot = new PdfDictionary(doc);
        annot.Elements.SetName("/Type", "/Annot");
        annot.Elements.SetName("/Subtype", "/FreeText");
        annot.Elements["/Rect"] = new PdfArray(doc, new PdfReal(100), new PdfReal(600), new PdfReal(300), new PdfReal(640));
        annot.Elements["/Contents"] = new PdfString("Hello");
        annot.Elements["/DA"] = new PdfString("0 g /F1 12 Tf");
        annot.Elements["/AP"] = ap;
        doc.Internals.AddObject(annot);
        page.Elements["/Annots"] = new PdfArray(doc, annot.Reference!);
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static IEnumerable<PdfDictionary> FindFonts(PdfSharp.Pdf.PdfDocument doc)
    {
        foreach (var obj in doc.Internals.GetAllObjects())
            if (obj is PdfDictionary d && d.Elements.GetName("/Type") == "/Font" && d.Elements.GetName("/Subtype") == "/Type0") yield return d;
    }
}
