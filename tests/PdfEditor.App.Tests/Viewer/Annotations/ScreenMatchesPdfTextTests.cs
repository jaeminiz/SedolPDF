using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Appearance;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfEditor.TestFixtures;
using PdfDoc = PdfSharp.Pdf.PdfDocument;

namespace PdfEditor.App.Tests.Viewer.Annotations;

/// <summary>
/// 설계서 4.2-3 (글자): Painter가 배치한 같은 글리프 목록을 화면(글리프 윤곽선 경로)과 PDF(CID 글꼴 + Tj)가 그렸을 때
/// PDFium 렌더와 Avalonia 오프스크린 렌더가 같은 모양인지 본다. ScreenMatchesPdfTests와 같은 허용치.
/// </summary>
public class ScreenMatchesPdfTextTests : IDisposable
{
    private const double Scale = 2;
    private const int ChannelTolerance = 40;
    private const double MaxMismatch = 0.02;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-scrtxt-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public ScreenMatchesPdfTextTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        AppearanceGeometry.TextFont = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string FontPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var f = Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf");
            if (File.Exists(f)) return f;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("assets/fonts/NotoSansKR-Regular.otf");
    }

    [Fact]
    public async Task TextRun_ScreenMatchesPdfium()
    {
        const float W = 200, H = 120;
        using var font = AppFont.Load(FontPath());
        AppearanceGeometry.TextFont = font;

        var laid = TextLayout.Layout(font, "Hello 가나다 World\nTest 한글 AVAT", 16f, 170f, TextAlign.Left, new PdfPointF(12, 105));
        Assert.Empty(laid.MissingCodepoints);
        var glyphs = laid.Lines.SelectMany(l => l).ToImmutableArray();
        var drawing = new AppearanceDrawing([], 1f, laid.Bounds)
        {
            Texts = [new TextRunOp("F1", 16f, new PdfColor(20, 20, 20), glyphs)],
        };

        // PDF: 같은 CID 글꼴 + 같은 내용 스트림
        var path = Path.Combine(_dir, "text.pdf");
        using (var doc = new PdfDoc())
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(W);
            page.Height = XUnit.FromPoint(H);
            var fontRef = FontEmbedder.Embed(doc, font, drawing.CollectFontUse());
            var fonts = new PdfDictionary(doc);
            fonts.Elements["/F1"] = fontRef;
            var res = new PdfDictionary(doc);
            res.Elements["/Font"] = fonts;
            page.Elements["/Resources"] = res;
            var content = new PdfDictionary(doc);
            content.CreateStream(System.Text.Encoding.ASCII.GetBytes(PdfContentWriter.Write(drawing)));
            doc.Internals.AddObject(content);
            page.Elements["/Contents"] = content.Reference!;
            doc.Save(path);
        }

        var (pdfium, bounds, w, h) = await _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            var size = new PageTransform(page.BoundsPt, page.Rotation, Scale).ViewSize;
            int pw = (int)Math.Ceiling(size.W), ph = (int)Math.Ceiling(size.H);
            return (page.RenderTile(Scale, 0, 0, pw, ph, withAnnotations: true), page.BoundsPt, pw, ph);
        });

        var tr = new PageTransform(bounds, 0, Scale);
        var screen = HeadlessUi.RenderBgra(w, h, ctx =>
        {
            using var _ = ctx.PushTransform(new Avalonia.Matrix(tr.Matrix.M11, tr.Matrix.M12, tr.Matrix.M21, tr.Matrix.M22,
                tr.Matrix.OffsetX, tr.Matrix.OffsetY));
            var cached = AppearanceGeometry.FromDrawing(drawing);
            Assert.NotEmpty(cached.Texts);
            cached.Draw(ctx, 1.0);
        });

        for (int i = 3; i < pdfium.Length; i += 4) { pdfium[i] = 255; screen[i] = 255; }
        Assert.True(CountNonWhite(pdfium) > 200, "PDFium render is blank");
        Assert.True(CountNonWhite(screen) > 200, "screen render is blank");
        var (ratio, max) = ImageCompare.Compare(pdfium, screen, w, h, ChannelTolerance);
        if (ratio > MaxMismatch)
        {
            string dump = Path.Combine(Path.GetTempPath(), "scr-text");
            PngFile.WriteBgra(dump + ".pdfium.png", pdfium, w, h);
            PngFile.WriteBgra(dump + ".screen.png", screen, w, h);
            Assert.Fail($"text: mismatch {ratio:P2} (allowed {MaxMismatch:P1}), max channel diff {max} ({dump}.*.png)");
        }
    }

    private static int CountNonWhite(byte[] b)
    {
        int n = 0;
        for (int i = 0; i < b.Length; i += 4)
            if (b[i] < 200 || b[i + 1] < 200 || b[i + 2] < 200) n++;
        return n;
    }
}
