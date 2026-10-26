using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Tests;

/// <summary>M5 Task 7: 대체 글꼴 줄(고딕/명조, 가짜 굵게·기울임)의 내용과 저장.</summary>
public sealed class FontChoiceTests : IDisposable
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static readonly Lazy<AppFont> Sans = new(() => AppFont.Load(Path.Combine(Root(), "assets", "fonts", "NotoSansKR-Regular.otf")));
    private static readonly Lazy<AppFont> Serif = new(() => AppFont.Load(Path.Combine(Root(), "assets", "fonts", "NotoSerifKR-Regular.otf")));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-fontchoice-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private static readonly PdfColor Black = new(0, 0, 0);

    public FontChoiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AppFontCatalog Catalog() => new(Sans.Value, () => Serif.Value);

    [Fact]
    public void BoldName_UsesSyntheticBold_RenderMode2()
    {
        var choice = Catalog().Choose(new PdfFontInfo("ABCDEF+MalgunGothic-Bold"));
        Assert.True(choice.SyntheticBold);
        var (spec, _) = AppFontLine.Build(choice, "굵게 Bold", 10, PdfMatrix.Translate(50, 700), Black);
        Assert.NotNull(spec);
        Assert.Contains("2 Tr", spec!.Content);
        Assert.Contains("0.3 w", spec.Content);          // 글자 크기 10 × 3 %
        Assert.Contains("0 0 0 RG", spec.Content);        // 선 색 = 글자 색
        Assert.Equal(0.3f, spec.BoldStrokePt, 3);
    }

    [Fact]
    public void Regular_HasNoRenderModeOrSkew()
    {
        var (spec, _) = AppFontLine.Build(Catalog().Choose(new PdfFontInfo("Arial")), "plain", 10, PdfMatrix.Translate(50, 700), Black);
        Assert.DoesNotContain("Tr", spec!.Content);
        Assert.DoesNotContain("0.21", spec.Content);
        Assert.Equal(0f, spec.BoldStrokePt);
    }

    [Fact]
    public void Italic_UsesSkew()
    {
        var plain = AppFontLine.Build(Sans.Value, "lIi", 10, PdfMatrix.Translate(0, 0), Black).Spec!;
        var choice = Catalog().Choose(new PdfFontInfo("Arial-ItalicMT"));
        Assert.True(choice.SyntheticItalic);
        var it = AppFontLine.Build(choice, "lIi", 10, PdfMatrix.Translate(0, 0), Black).Spec!;
        Assert.Contains("1 0 0.21 1 0 0 cm", it.Content);
        // 윗부분(y가 큰 점)일수록 오른쪽으로 밀린다: 같은 윤곽선 점의 x 변위 = 0.21 × y
        Assert.Equal(plain.Outline.Length, it.Outline.Length);
        for (int i = 0; i < plain.Outline.Length; i++)
        {
            if (plain.Outline[i].Op is 'Z') continue;
            var a = plain.Outline[i].Op == 'C' ? plain.Outline[i].C : plain.Outline[i].A;
            var b = it.Outline[i].Op == 'C' ? it.Outline[i].C : it.Outline[i].A;
            Assert.Equal(0.21f * a.Y, b.X - a.X, 2);
            Assert.Equal(a.Y, b.Y, 3);
        }
    }

    [Fact]
    public void SerifLine_CarriesSerifFamily_AndSerifGlyphs()
    {
        var choice = Catalog().Choose(new PdfFontInfo("Batang"));
        Assert.Equal(AppFontFamily.Serif, choice.Family);
        var spec = AppFontLine.Build(choice, "명조체 문단", 11, PdfMatrix.Translate(50, 700), Black).Spec!;
        Assert.Equal(PdfEditor.Document.Annotations.Patching.FontUse.SerifFamily, spec.Font.Family);
        var sans = AppFontLine.Build(Sans.Value, "명조체 문단", 11, PdfMatrix.Translate(50, 700), Black).Spec!;
        Assert.Equal("", sans.Font.Family);
        Assert.NotEmpty(sans.Outline);
        Assert.NotEqual(sans.Outline[1].A, spec.Outline[1].A);   // 서로 다른 글꼴의 윤곽선
    }

    // ---- 저장: 명조 줄은 Noto Serif KR 서브셋으로, 고딕 줄과 같은 쪽에 함께 ----

    private async Task<DocumentSession> Open()
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateDocumentLike(p, false);
        return await DocumentSession.OpenAsync(_t, p, null);
    }

    private static async Task Add(DocumentSession s, AppTextSpec spec) =>
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new InsertAppTextEdit(s.Content.NewId(), spec)]));

    private (string Text, List<string> Fonts) Saved(string path)
    {
        string text = _t.InvokeAsync(() =>
        {
            using var d = PdfDocument.Open(_t, path, null);
            using var p = d.LoadPage(0);
            using var tp = p.LoadText();
            return tp.GetText(0, tp.CharCount);
        }).GetAwaiter().GetResult();
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var fonts = doc.Pages[0].Resources.Elements.GetDictionary("/Font")!.Elements
            .Select(kv => (kv.Value is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : kv.Value) as PdfSharp.Pdf.PdfDictionary)
            .Select(d => d?.Elements.GetName("/BaseFont") ?? "").ToList();
        return (text, fonts);
    }

    private PdfEditor.Document.Saving.SaveService Svc(bool withSerif = true, AppFontCatalog? c = null) =>
        new([], null, new PdfSharpAnnotationPatcher(Sans.Value) { SerifFont = withSerif ? () => Serif.Value : null });
    [Fact]
    public async Task SerifAndSansLines_SaveBothFonts_TextExtractable()
    {
        await using var s = await Open();
        var cat = Catalog();
        await Add(s, AppFontLine.Build(cat.Choose(new PdfFontInfo("Batang")), "명조 줄 Serif", 12, PdfMatrix.Translate(100, 500), Black).Spec!);
        await Add(s, AppFontLine.Build(cat.Choose(new PdfFontInfo("Arial-BoldMT")), "고딕 줄 Sans", 12, PdfMatrix.Translate(100, 450), Black).Spec!);
        await Svc().SaveAsync(s, Path.Combine(_dir, "o.pdf"));
        var (text, fonts) = Saved(Path.Combine(_dir, "o.pdf"));
        Assert.Contains("명조 줄 Serif", text);
        Assert.Contains("고딕 줄 Sans", text);
        Assert.Single(fonts, f => f.Contains("NotoSerifKR-Regular"));
        Assert.Single(fonts, f => f.Contains("NotoSansKR-Regular"));
    }

    [Fact]
    public async Task SerifSave_WithoutSerifFont_FailsLoudly()
    {
        await using var s = await Open();
        await Add(s, AppFontLine.Build(Catalog().Choose(new PdfFontInfo("Batang")), "명조", 12, PdfMatrix.Translate(100, 500), Black).Spec!);
        await Assert.ThrowsAnyAsync<Exception>(() => Svc(withSerif: false).SaveAsync(s, Path.Combine(_dir, "x.pdf")));
    }

    [Fact]
    public async Task BoldItalicLine_SavesAndRendersInkWithoutCrash()
    {
        await using var s = await Open();
        var choice = Catalog().Choose(new PdfFontInfo("Times-BoldItalic"));
        Assert.True(choice.SyntheticBold && choice.SyntheticItalic && choice.Family == AppFontFamily.Serif);
        await Add(s, AppFontLine.Build(choice, "굵은 기울임 Bold Italic", 14, PdfMatrix.Translate(100, 500), Black).Spec!);
        await Svc().SaveAsync(s, Path.Combine(_dir, "bi.pdf"));
        Assert.Contains("굵은 기울임 Bold Italic", Saved(Path.Combine(_dir, "bi.pdf")).Text);
    }
}
