using System.Runtime.CompilerServices;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Tests.Fonts;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.Appearance.Tests;

/// <summary>
/// 0.5.0 형광펜 호환 시료: `spikes/out/v050-highlight-weblike.pdf`·`-rotated.pdf`(+ PNG)와 전후 비교 `docs/design/highlight-before-after.png`를 만든다
/// (docs/checklists/viewer-compat.md 0.5.0 형광펜 절). 폴더가 없으면 만들지 않고 넘어간다. 시료는 합성 PDF이며 사용자 파일이 아니다.
/// </summary>
public class V050HighlightSampleTests : IDisposable
{
    private const double Scale = 2;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-v050hl-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private static readonly AppFont Font = AppFont.Load(FontTestSupport.FontPath());
    private static readonly AnnotationAssets Assets = new();

    public V050HighlightSampleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Root([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static readonly string[] Lines =
    [
        "SEDOL Analytics & Intelligent PDF Engine",
        "글로벌 런던증권거래소 SEDOL 식별 코드 기반 실시간 주가",
        "Underline sample: Roboto-like text 밑줄 시료",
        "Strike-out sample: 취소선 시료 글자 간격이 넓음",
    ];

    private async Task<(byte[] Bgra, int W, int H)> BuildAsync(string pdf, string target)
    {
        await using (var s = await DocumentSession.OpenAsync(_t, pdf, null))
        {
            await s.AnnotationsReady;
            var id = s.Pages[0].Id;
            var all = await _t.InvokeAsync(() =>
            {
                var tp = s.GetLoadedText(id);
                return (IReadOnlyList<PdfRectF>)[.. tp.GetMarkupRects(0, tp.CharCount).Select(PdfRectF.FromLtrbRectangle)];
            });
            // 글줄별로 나눈다(줄 간격 40pt: 첫 줄 기준선 위 100pt).
            var byLine = all.GroupBy(r => (int)Math.Round((842 - (r.Bottom + r.Top) / 2f - 100) / 40f)).OrderBy(g => g.Key).ToList();
            Annotation A(AnnotKind kind, IEnumerable<PdfRectF> rects, PdfColor color)
            {
                var q = TextMarkupGeometry.ToQuads([.. rects]);
                return new Annotation
                {
                    Id = s.Annotations.NewId(), Kind = kind, Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print,
                    Color = color, Opacity = 1f, Quads = [.. q], Rect = q.Select(x => x.Bounds).Aggregate((a, b) => a.Union(b)),
                };
            }
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, A(AnnotKind.Highlight, byLine[0].Concat(byLine[1]), new PdfColor(255, 255, 0))));
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, A(AnnotKind.Underline, byLine[2], new PdfColor(0, 95, 184))));
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, A(AnnotKind.StrikeOut, byLine[3], new PdfColor(229, 55, 45))));
            await new SaveService([], new PdfAppearanceSource(Font, Assets), new PdfEditor.Appearance.Patching.PdfSharpAnnotationPatcher(Font), Assets).SaveAsync(s, target);
        }
        return await Render(target, true);
    }

    private Task<(byte[] Bgra, int W, int H)> Render(string pdf, bool annots) => _t.InvokeAsync(() =>
    {
        using var doc = PdfDocument.Open(_t, pdf);
        using var page = doc.LoadPage(0);
        var size = new PageTransform(page.BoundsPt, page.Rotation, Scale).ViewSize;
        int w = (int)Math.Ceiling(size.W), h = (int)Math.Ceiling(size.H);
        return (page.RenderTile(Scale, 0, 0, w, h, withAnnotations: annots), w, h);
    });

    [Fact]
    public async Task CreateV050HighlightSamples()
    {
        string root = Root();
        string outDir = Path.Combine(root, "spikes", "out");
        TestPdfFactory.CreateWebLikeText(Path.Combine(_dir, "web.pdf"), Lines);
        TestPdfFactory.CreateWebLikeText(Path.Combine(_dir, "rot.pdf"), Lines, rotate: 90);
        var web = await BuildAsync(Path.Combine(_dir, "web.pdf"), Path.Combine(_dir, "web-out.pdf"));
        var rot = await BuildAsync(Path.Combine(_dir, "rot.pdf"), Path.Combine(_dir, "rot-out.pdf"));
        Assert.True(web.W > 0 && rot.W > 0);
        if (!Directory.Exists(outDir)) return;

        File.Copy(Path.Combine(_dir, "web-out.pdf"), Path.Combine(outDir, "v050-highlight-weblike.pdf"), true);
        File.Copy(Path.Combine(_dir, "rot-out.pdf"), Path.Combine(outDir, "v050-highlight-rotated.pdf"), true);
        PngFile.WriteBgra(Path.Combine(outDir, "v050-highlight-weblike.png"), web.Bgra, web.W, web.H);
        PngFile.WriteBgra(Path.Combine(outDir, "v050-highlight-rotated.png"), rot.Bgra, rot.W, rot.H);

        // 전후 비교: 위 두 줄(강조 영역)만 잘라 나란히. 이전 모양은 옛 방식(낱말 조각 사각형 + 반투명 노랑 덮기)을 같은 쪽 위에 그대로 흉내 낸 것.
        string docsDesign = Path.Combine(root, "docs", "design");
        if (!Directory.Exists(docsDesign)) return;
        var plain = await Render(Path.Combine(_dir, "web.pdf"), false);
        var before = (byte[])plain.Bgra.Clone();
        var oldRects = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, Path.Combine(_dir, "web.pdf"));
            using var page = doc.LoadPage(0);
            using var tp = page.LoadText();
            return tp.GetRects(0, tp.CharCount).Select(PdfRectF.FromLtrbRectangle).ToList();
        });
        foreach (var r in oldRects.Where(r => 842 - (r.Bottom + r.Top) / 2f < 100 + 40 * 1.5f))
            AlphaOver(before, plain.W, r, (255, 214, 10), 0.5);
        int cropTop = (int)((842 - 770) * Scale), cropH = (int)(100 * Scale), w = web.W, gap = 12;
        var combo = new byte[w * (cropH * 2 + gap) * 4];
        Array.Fill(combo, (byte)255);
        Array.Copy(before, cropTop * w * 4, combo, 0, cropH * w * 4);
        Array.Copy(web.Bgra, cropTop * w * 4, combo, (cropH + gap) * w * 4, cropH * w * 4);
        PngFile.WriteBgra(Path.Combine(docsDesign, "highlight-before-after.png"), combo, w, cropH * 2 + gap);
    }

    private static void AlphaOver(byte[] bgra, int w, PdfRectF r, (byte R, byte G, byte B) c, double a)
    {
        int x0 = (int)(r.Left * Scale), x1 = (int)Math.Ceiling(r.Right * Scale);
        int y0 = (int)((842 - r.Top) * Scale), y1 = (int)Math.Ceiling((842 - r.Bottom) * Scale);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int o = (y * w + x) * 4;
                bgra[o] = (byte)(bgra[o] * (1 - a) + c.B * a);
                bgra[o + 1] = (byte)(bgra[o + 1] * (1 - a) + c.G * a);
                bgra[o + 2] = (byte)(bgra[o + 2] * (1 - a) + c.R * a);
            }
    }
}
