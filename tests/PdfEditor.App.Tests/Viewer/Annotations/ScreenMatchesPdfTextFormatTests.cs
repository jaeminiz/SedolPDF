using Pdf.Native;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Appearance;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfDoc = PdfSharp.Pdf.PdfDocument;

namespace PdfEditor.App.Tests.Viewer.Annotations;

/// <summary>
/// 0.5.0 글자 서식: 굵게·기울임·밑줄·취소선·명조를 입힌 텍스트 상자를 Painter가 한 번 그린 결과를 화면(Avalonia 글리프 윤곽선 + 선)과
/// PDF(AP 내용 스트림)가 그렸을 때 같은 모양인지 본다(ScreenMatchesPdfTextTests와 같은 방식·허용치).
/// </summary>
public class ScreenMatchesPdfTextFormatTests : IDisposable
{
    private const double Scale = 2;
    private const double MaxInkMismatch = 0.60;   // ScreenMatchesPdfTextTests와 같다(Skia·PDFium 안티앨리어싱 차이 포함)

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-scrfmt-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _sans = AppFont.Load(FontFile("NotoSansKR-Regular.otf"));
    private readonly AppFont _serif = AppFont.Load(FontFile("NotoSerifKR-Regular.otf"));

    public ScreenMatchesPdfTextFormatTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        AppearanceGeometry.TextFont = null;
        AppearanceGeometry.SerifFont = null;
        _sans.Dispose();
        _serif.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string FontFile(string n)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var f = Path.Combine(dir.FullName, "assets", "fonts", n);
            if (File.Exists(f)) return f;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(n);
    }

    private static Annotation Box(Func<Annotation, Annotation> f) => f(new Annotation
    {
        Kind = AnnotKind.FreeText, Rect = new PdfRectF(8, 8, 192, 112), Contents = "Hello 가나다 World\nTest 한글 AVAT", FontSizePt = 16f,
        TextColor = new PdfColor(20, 20, 20), Color = null, FillColor = null, BorderWidth = 0f,
    });

    private async Task<(double Ratio, byte[] Pdfium, byte[] Screen, int W, int H)> CompareAsync(Annotation a, string name)
    {
        AppearanceGeometry.TextFont = _sans;
        AppearanceGeometry.SerifFont = _serif;
        var drawing = AnnotationPainter.Paint(a, 0, new PaintContext(_sans, AnnotationAssets.Empty, _serif));
        const float W = 200, H = 120;
        var path = Path.Combine(_dir, name + ".pdf");
        using (var doc = new PdfDoc())
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(W);
            page.Height = XUnit.FromPoint(H);
            var use = drawing.CollectFontUse();
            var fontRef = FontEmbedder.Embed(doc, use.Family == PdfEditor.Document.Annotations.Patching.FontUse.SerifFamily ? _serif : _sans, use);
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
            using var _ = ctx.PushTransform(new Avalonia.Matrix(tr.Matrix.M11, tr.Matrix.M12, tr.Matrix.M21, tr.Matrix.M22, tr.Matrix.OffsetX, tr.Matrix.OffsetY));
            AppearanceGeometry.FromDrawing(drawing).Draw(ctx);
        });
        for (int i = 3; i < pdfium.Length; i += 4) { pdfium[i] = 255; screen[i] = 255; }
        return (InkMismatch(pdfium, screen), pdfium, screen, w, h);
    }

    private static int Ink(byte[] bgra) { int n = 0; for (int i = 0; i < bgra.Length; i += 4) if (bgra[i] < 160 && bgra[i + 1] < 160 && bgra[i + 2] < 160) n++; return n; }

    private static double InkMismatch(byte[] a, byte[] b)
    {
        int union = 0, diff = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            bool ia = a[i] < 160 && a[i + 1] < 160 && a[i + 2] < 160, ib = b[i] < 160 && b[i + 1] < 160 && b[i + 2] < 160;
            if (!ia && !ib) continue;
            union++;
            if (Math.Abs(a[i] - b[i]) > 40 || Math.Abs(a[i + 1] - b[i + 1]) > 40 || Math.Abs(a[i + 2] - b[i + 2]) > 40) diff++;
        }
        return union == 0 ? 0 : (double)diff / union;
    }

    public static IEnumerable<object[]> Formats() =>
    [
        ["plain", false, false, false, false, TextFontFamily.Sans],
        ["bold", true, false, false, false, TextFontFamily.Sans],
        ["italic", false, true, false, false, TextFontFamily.Sans],
        ["underline", false, false, true, false, TextFontFamily.Sans],
        ["strike", false, false, false, true, TextFontFamily.Sans],
        ["serif", false, false, false, false, TextFontFamily.Serif],
        ["all", true, true, true, true, TextFontFamily.Serif],
    ];

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Format_ScreenMatchesPdfium(string name, bool bold, bool italic, bool underline, bool strike, TextFontFamily family)
    {
        var a = Box(x => x with { Bold = bold, Italic = italic, Underline = underline, StrikeThrough = strike, TextFamily = family });
        var (ratio, pdfium, screen, w, h) = await CompareAsync(a, name);
        Assert.True(Ink(pdfium) > 200, "PDFium render is blank");
        Assert.True(Ink(screen) > 200, "screen render is blank");
        if (ratio > MaxInkMismatch)
        {
            string dump = Path.Combine(Path.GetTempPath(), "scr-fmt-" + name);
            PngFile.WriteBgra(dump + ".pdfium.png", pdfium, w, h);
            PngFile.WriteBgra(dump + ".screen.png", screen, w, h);
            Assert.Fail($"{name}: ink mismatch {ratio:P2} (allowed {MaxInkMismatch:P1}) ({dump}.*.png)");
        }
    }

    [Fact]
    public async Task Format_ChangesThePicture_OnBothSides()
    {
        var (_, plainP, plainS, _, _) = await CompareAsync(Box(x => x), "c-plain");
        var (_, boldP, boldS, _, _) = await CompareAsync(Box(x => x with { Bold = true }), "c-bold");
        var (_, ulP, ulS, _, _) = await CompareAsync(Box(x => x with { Underline = true }), "c-ul");
        Assert.True(Ink(boldP) > Ink(plainP) * 1.05, "bold should add ink in the PDF render");
        Assert.True(Ink(boldS) > Ink(plainS) * 1.05, "bold should add ink on screen");
        Assert.True(Ink(ulP) > Ink(plainP), "underline should add ink in the PDF render");
        Assert.True(Ink(ulS) > Ink(plainS), "underline should add ink on screen");
    }

    [Fact]
    public async Task Serif_FellBack_WhenNoSerifFont_UsesSansOnBothSides()
    {
        // 명조 파일이 없으면 화면·AP 모두 고딕으로 그린다(Family가 "").
        var a = Box(x => x with { TextFamily = TextFontFamily.Serif });
        var d = AnnotationPainter.Paint(a, 0, new PaintContext(_sans, AnnotationAssets.Empty));
        Assert.Equal("", d.Texts[0].Family);
        Assert.Equal("", d.CollectFontUse().Family);
        await Task.CompletedTask;
    }
}
