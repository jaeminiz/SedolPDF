using System.Text;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Tests.Fonts;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.Appearance.Tests;

/// <summary>
/// 0.5.0 형광펜: 텍스트 선택 → 줄별 한 사각형(QuadPoints) + 곱하기 블렌드. 저장 → 다시 열기 → PDFium 렌더로 글자 색이 보존되는지 본다.
/// </summary>
public class HighlightTextTests : IDisposable
{
    private const double Scale = 2;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-hltext-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private static readonly AppFont Font = AppFont.Load(FontTestSupport.FontPath());
    private static readonly AnnotationAssets Assets = new();
    private static readonly PdfColor Yellow = new(255, 255, 0);

    public HighlightTextTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private static SaveService NewSaver() => new([], new PdfAppearanceSource(Font, Assets), new PdfEditor.Appearance.Patching.PdfSharpAnnotationPatcher(Font), Assets);

    /// <summary>쪽 0의 글자 전부를 PDFium 마크업 상자로 얻는다(앱이 선택 → 마크업 만들 때와 같은 호출).</summary>
    private async Task<IReadOnlyList<PdfRectF>> MarkupRectsAsync(string pdf)
    {
        await using var s = await DocumentSession.OpenAsync(_t, pdf, null);
        var id = s.Pages[0].Id;
        return await _t.InvokeAsync(() =>
        {
            var tp = s.GetLoadedText(id);
            return (IReadOnlyList<PdfRectF>)[.. tp.GetMarkupRects(0, tp.CharCount).Select(PdfRectF.FromLtrbRectangle)];
        });
    }

    private async Task<(byte[] Bgra, int W, int H, string Saved)> HighlightAndRenderAsync(string pdf, IReadOnlyList<PdfRectF> rects, float opacity, string outName)
    {
        var target = P(outName);
        await using (var s = await DocumentSession.OpenAsync(_t, pdf, null))
        {
            await s.AnnotationsReady;
            var id = s.Pages[0].Id;
            var quads = TextMarkupGeometry.ToQuads(rects);
            var a = new Annotation
            {
                Id = s.Annotations.NewId(), Kind = AnnotKind.Highlight, Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print,
                Color = Yellow, Opacity = opacity, Quads = [.. quads], Rect = quads.Select(q => q.Bounds).Aggregate((x, y) => x.Union(y)),
            };
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, a));
            await NewSaver().SaveAsync(s, target);
        }
        return await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, target);
            using var page = doc.LoadPage(0);
            var size = new PageTransform(page.BoundsPt, page.Rotation, Scale).ViewSize;
            int w = (int)Math.Ceiling(size.W), h = (int)Math.Ceiling(size.H);
            return (page.RenderTile(Scale, 0, 0, w, h, withAnnotations: true), w, h, target);
        });
    }

    private static (int Dark, int Yellow) Count(byte[] b)
    {
        int dark = 0, yellow = 0;
        for (int i = 0; i < b.Length; i += 4)
        {
            byte bl = b[i], g = b[i + 1], r = b[i + 2];
            if (r < 70 && g < 70 && bl < 70) dark++;                // 글자(검정 그대로)
            else if (r > 235 && g > 235 && bl < 60) yellow++;       // 바탕 노랑
        }
        return (dark, yellow);
    }

    /// <summary>주석 없이 그린 같은 쪽의 검은 글자 픽셀 수(기준).</summary>
    private Task<int> PlainDarkAsync(string pdf) => _t.InvokeAsync(() =>
    {
        using var doc = PdfDocument.Open(_t, pdf);
        using var page = doc.LoadPage(0);
        var size = new PageTransform(page.BoundsPt, page.Rotation, Scale).ViewSize;
        return Count(page.RenderTile(Scale, 0, 0, (int)Math.Ceiling(size.W), (int)Math.Ceiling(size.H), withAnnotations: true)).Dark;
    });

    [Theory]
    [InlineData(1f)]
    [InlineData(0.5f)]
    public async Task Highlight_MultipliesOverText_GlyphStaysBlack(float opacity)
    {
        TestPdfFactory.CreateTextLines(P("t.pdf"), ["HIGHLIGHT MULTIPLY TEST"]);
        var rects = await MarkupRectsAsync(P("t.pdf"));
        var (bgra, _, _, saved) = await HighlightAndRenderAsync(P("t.pdf"), rects, opacity, "out.pdf");

        var (dark, yellow) = Count(bgra);
        int plain = await PlainDarkAsync(P("t.pdf"));
        // 글자 픽셀은 검정(곱하기: 검정 x 노랑 = 검정)으로 남는다. 알파 덮기라면 글자 한가운데가 올리브가 되어 검은 픽셀이 거의 사라진다.
        Assert.True(plain > 400, $"baseline has text: {plain}");
        Assert.True(dark >= plain * 0.85, $"glyph cores stay black: dark={dark}, plain={plain}");
        if (opacity >= 1f) Assert.True(yellow > 3000, $"vivid yellow background: {yellow}");

        // 저장된 AP에 곱하기 블렌드가 들어 있다(다른 뷰어도 같은 모양으로 그리도록).
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(saved));
        Assert.Contains("/BM", text);
        Assert.Contains("/Multiply", text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task WebLikeLines_OneQuadPerLine_CoversWordGapsAndHangul(int rotate)
    {
        TestPdfFactory.CreateWebLikeText(P("w.pdf"), ["SEDOL Analytics & Intelligent PDF Engine", "글로벌 런던증권거래소 SEDOL 식별 코드"], rotate);
        var rects = await MarkupRectsAsync(P("w.pdf"));
        var quads = TextMarkupGeometry.ToQuads(rects);

        Assert.Equal(2, quads.Count);                                    // 줄마다 사각형 하나(낱말·글자별 조각이 아니다)
        var l1 = quads[0].Bounds; var l2 = quads[1].Bounds;
        Assert.True(l1.Width > 440 && l2.Width > 400, $"line widths {l1.Width}, {l2.Width}");       // 공백·글자 사이 틈까지 이어졌다
        Assert.True(l1.Bottom > l2.Top, "lines are separate rows");
        Assert.True(l1.Height > 20 && l2.Height > 20, $"line heights {l1.Height}, {l2.Height}");     // 글꼴 상하 여백까지 담긴 줄 높이
        Assert.True(Math.Abs(l1.Left - 40) < 2 && Math.Abs(l2.Left - 40) < 2);
    }

    [Fact]
    public async Task WebLikeLines_Highlight_ColorsGapsAndKeepsText()
    {
        TestPdfFactory.CreateWebLikeText(P("w2.pdf"), ["SEDOL Analytics & Intelligent PDF Engine", "글로벌 런던증권거래소 SEDOL 식별 코드"]);
        var rects = await MarkupRectsAsync(P("w2.pdf"));
        var (bgra, w, h, _) = await HighlightAndRenderAsync(P("w2.pdf"), rects, 1f, "w2out.pdf");
        var (dark, yellow) = Count(bgra);
        int plain = await PlainDarkAsync(P("w2.pdf"));
        Assert.True(plain > 1000 && dark >= plain * 0.85, $"dark={dark}, plain={plain}");

        // 낱말 사이 빈 자리(첫 줄 "SEDOL" 뒤 틈, x≈110pt, 줄 중간 높이 y≈748pt)도 노랗게 칠해진다.
        int px = (int)(112 * Scale), py = (int)((842 - 748) * Scale);
        int o = (py * w + px) * 4;
        Assert.True(bgra[o + 2] > 235 && bgra[o + 1] > 235 && bgra[o] < 60, $"gap pixel is yellow: B{bgra[o]} G{bgra[o + 1]} R{bgra[o + 2]}");
    }
}
