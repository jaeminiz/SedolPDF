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
/// 0.12.0 서식 보존: Acrobat 타자기 글상자는 /DS 없이 /RC의 span에만 서식(맑은 고딕·10pt·굵게·파랑)이 있고 /DA는 빨강뿐이다.
/// 읽기·이동·크기 조절·내용 수정 뒤에도 서식이 유지되는지 확인한다(docs/design/0.12.0-format-preservation.md).
/// </summary>
public class FormatPreservationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-fmtp-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _font = AppFont.Load(FontPath());
    private readonly PdfAppearanceSource _ap;
    private readonly PdfSharpAnnotationPatcher _patcher;

    private static readonly PdfColor Blue = new(0, 0, 255);

    public FormatPreservationTests()
    {
        Directory.CreateDirectory(_dir);
        _ap = new PdfAppearanceSource(_font);
        _patcher = new PdfSharpAnnotationPatcher(_font);
    }

    private static string FontPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) return Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf");
        throw new FileNotFoundException("assets/fonts/NotoSansKR-Regular.otf");
    }

    public void Dispose()
    {
        _t.Dispose();
        _patcher.Dispose();
        _font.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "freetext-rc-only.pdf");

    private string P(string n) => Path.Combine(_dir, n);

    private SaveService Svc() => new([], _ap, _patcher);

    private static Annotation ReadFixture()
        => AnnotationReader.Read(File.ReadAllBytes(Fixture), null, [0])[0].Single().Data!;

    private static PdfDictionary SavedAnnot(string path, PdfSharp.Pdf.PdfDocument doc)
    {
        var annots = (PdfArray)((doc.Pages[0].Elements["/Annots"] as PdfReference)?.Value ?? doc.Pages[0].Elements["/Annots"])!;
        var item = annots.Elements[0];
        return (PdfDictionary)((item as PdfReference)?.Value ?? item)!;
    }

    private static string RcOf(PdfDictionary d) => ((PdfString)d.Elements["/RC"]!).Value;

    private static byte[] ApBytes(PdfDictionary d)
    {
        var ap = (PdfDictionary)((d.Elements["/AP"] as PdfReference)?.Value ?? d.Elements["/AP"])!;
        var n = (PdfDictionary)((ap.Elements["/N"] as PdfReference)?.Value ?? ap.Elements["/N"])!;
        return n.Stream!.UnfilteredValue;
    }

    private static double[] Rect(PdfDictionary d)
        => ((PdfArray)d.Elements["/Rect"]!).Elements.Select(i => i is PdfInteger n ? n.Value : ((PdfReal)i).Value).ToArray();

    private async Task<string> EditAndSave(string name, Func<Annotation, Annotation> edit)
    {
        await using var s = await DocumentSession.OpenAsync(_t, Fixture, null);
        await s.AnnotationsReady;
        var a = s.Annotations.Get(s.Pages[0].Id).Items.Single().Data;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(s.Pages[0].Id, [(a, edit(a))]));
        string target = P(name);
        await Svc().SaveAsync(s, target);
        return target;
    }

    [Fact]
    public void RcOnlyFreeText_ReadsSpanStyle_NotBodyOrDa()
    {
        var a = ReadFixture();
        Assert.Equal(FreeTextIntent.Typewriter, a.Intent);
        Assert.Equal(Blue, a.TextColor);          // /DA는 빨강, /RC body는 검정 — 실제 글자는 span의 파랑
        Assert.Equal(10f, a.FontSizePt);          // body는 12pt
        Assert.True(a.Bold);                      // font-weight:700
        Assert.False(a.Italic);
        Assert.Equal("맑은 고딕", a.FontName);
        Assert.Equal(TextFontFamily.Sans, a.TextFamily);
    }

    [Fact]
    public async Task Moved_KeepsFormat_AndOriginalAppearanceAndRc()
    {
        var src = PdfReader.Open(Fixture, PdfDocumentOpenMode.Import);
        var srcAnnot = SavedAnnot(Fixture, src);
        var srcRect = Rect(srcAnnot);
        byte[] srcAp = ApBytes(srcAnnot);
        string srcRc = RcOf(srcAnnot);

        string target = await EditAndSave("moved.pdf", a => a with { Rect = new PdfRectF(a.Rect.Left + 30, a.Rect.Bottom + 20, a.Rect.Right + 30, a.Rect.Top + 20) });

        var back = ReadBack(target);
        Assert.Equal(Blue, back.TextColor);
        Assert.Equal(10f, back.FontSizePt);
        Assert.True(back.Bold);
        Assert.Equal("맑은 고딕", back.FontName);
        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var d = SavedAnnot(target, doc);
        var r = Rect(d);
        Assert.Equal(srcRect[0] + 30, r[0], 0.01);
        Assert.Equal(srcRect[1] + 20, r[1], 0.01);
        Assert.Equal(srcRect[2] + 30, r[2], 0.01);
        Assert.Equal(srcRect[3] + 20, r[3], 0.01);
        Assert.Equal(srcAp, ApBytes(d));      // 모양은 다시 그리지 않았다
        Assert.Equal(srcRc, RcOf(d));
        Assert.Equal("1.000 0.000 0.000 rg", ((PdfString)d.Elements["/DA"]!).Value);
    }

    [Fact]
    public async Task Resized_Redrawn_ButFormatAndRichTextKept()
    {
        string target = await EditAndSave("resized.pdf", a => a with { Rect = new PdfRectF(a.Rect.Left, a.Rect.Bottom, a.Rect.Right - 100, a.Rect.Top) });
        var back = ReadBack(target);
        Assert.Equal(Blue, back.TextColor);
        Assert.Equal(10f, back.FontSizePt);
        Assert.True(back.Bold);
        Assert.Equal("맑은 고딕", back.FontName);
        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.Contains("font-weight:700", RcOf(SavedAnnot(target, doc)));   // 내용·서식이 같으면 원본 /RC를 그대로 둔다
    }

    [Fact]
    public async Task ContentEdited_RewritesWithOriginalFontName()
    {
        string target = await EditAndSave("edited.pdf", a => a with { Contents = "Changed text" });
        var back = ReadBack(target);
        Assert.Equal(Blue, back.TextColor);
        Assert.Equal(10f, back.FontSizePt);
        Assert.True(back.Bold);
        Assert.Equal("맑은 고딕", back.FontName);
        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var d = SavedAnnot(target, doc);
        Assert.Contains("Changed text", RcOf(d));
        Assert.Contains("맑은 고딕", RcOf(d));
        Assert.Contains("맑은 고딕", ((PdfString)d.Elements["/DS"]!).Value);
    }

    private static Annotation ReadBack(string path)
        => AnnotationReader.Read(File.ReadAllBytes(path), null, [0])[0].Single(o => o.Data is not null).Data!;
}
