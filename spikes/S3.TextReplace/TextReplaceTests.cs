using Pdf.Native;
using S2.FontSubset;

namespace S3.TextReplace;

public class TextReplaceTests : IDisposable
{
    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "s3-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Noto = Fonts.LoadNoto();

    public TextReplaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static int[] Cps(string s) => s.EnumerateRunes().Select(r => r.Value).ToArray();

    /// <summary>"ABC123" 글자만 든 서브셋 폰트로 쓴 텍스트 한 줄짜리 PDF를 만든다.</summary>
    private async Task<string> MakeSubsetTextPdf(string name)
    {
        var path = Path.Combine(_dir, name);
        var subset = FontSubsetter.Subset(Noto, Cps("ABC123"));
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.CreateNew(_t);
            doc.AddPage(595, 842);
            using var page = doc.LoadPage(0);
            TextReplacer.CreateText(doc, page, subset, "ABC123", 24f, 72, 700);
            doc.SaveCopy(path);
        });
        return path;
    }

    [Fact]
    public async Task ReplaceText_GlyphsInSubset_RendersSame()
    {
        var original = await MakeSubsetTextPdf("orig.pdf");
        var edited = Path.Combine(_dir, "edited.pdf");
        var extracted = await _t.InvokeAsync(() =>
        {
            using (var doc = PdfDocument.Open(_t, original))
            {
                using var page = doc.LoadPage(0);
                var obj = TextReplacer.FindFirstTextObject(page);
                Assert.True(TextReplacer.SetText(page, obj, "CBA321"));
                doc.SaveCopy(edited);
            }
            using var doc2 = PdfDocument.Open(_t, edited);
            using var page2 = doc2.LoadPage(0);
            return TextReplacer.ExtractText(page2);
        });
        Assert.Equal("CBA321", extracted);
    }

    [Fact]
    public async Task ReplaceText_GlyphMissingFromSubset_IsDetected()
    {
        var original = await MakeSubsetTextPdf("orig2.pdf");
        var (missingKorean, missingNone) = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, original);
            using var page = doc.LoadPage(0);
            var obj = TextReplacer.FindFirstTextObject(page);
            return (TextReplacer.MissingGlyphs(obj, "ABC가"), TextReplacer.MissingGlyphs(obj, "CBA321"));
        });
        Assert.Equal(new[] { (int)'가' }, missingKorean);
        Assert.Empty(missingNone);
    }

    [Fact]
    public async Task ReplaceText_Fallback_NewTextObjectWithAppFont()
    {
        var original = await MakeSubsetTextPdf("orig3.pdf");
        var edited = Path.Combine(TestOut.Dir(), "s3-fallback.pdf");
        var (extracted, objCount, sameMatrix) = await _t.InvokeAsync(() =>
        {
            bool same;
            using (var doc = PdfDocument.Open(_t, original))
            {
                using var page = doc.LoadPage(0);
                var obj = TextReplacer.FindFirstTextObject(page);
                var before = TextReplacer.GetMatrix(obj);
                var newObj = TextReplacer.ReplaceWithFallback(doc, page, obj, "ABC가", Noto);
                var after = TextReplacer.GetMatrix(newObj);
                same = Math.Abs(before.E - after.E) < 0.01 && Math.Abs(before.F - after.F) < 0.01
                       && Math.Abs(before.A - after.A) < 0.001 && Math.Abs(before.D - after.D) < 0.001;
                doc.SaveCopy(edited);
            }
            using var doc2 = PdfDocument.Open(_t, edited);
            using var page2 = doc2.LoadPage(0);
            return (TextReplacer.ExtractText(page2), TextReplacer.TextObjectCount(page2), same);
        });
        Assert.Equal("ABC가", extracted);
        Assert.Equal(1, objCount);
        Assert.True(sameMatrix, "위치·크기 행렬이 원래 객체와 같아야 함");
    }
}

public class TextReplaceRenderTests : IDisposable
{
    private readonly PdfiumThread _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task ReplaceText_FallbackResult_RendersInk()
    {
        var path = Path.Combine(TestOut.Dir(), "s3-fallback.pdf");
        if (!File.Exists(path)) throw new FileNotFoundException("먼저 ReplaceText_Fallback_NewTextObjectWithAppFont 실행 필요");
        var px = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            return page.RenderTile(1.0, 60, 842 - 740, 200, 60, false);
        });
        int non = 0;
        for (int i = 0; i < px.Length; i += 4) if (px[i] != 255 || px[i + 1] != 255 || px[i + 2] != 255) non++;
        Assert.True(non > 200, $"비흰색 픽셀 {non}");
    }
}
