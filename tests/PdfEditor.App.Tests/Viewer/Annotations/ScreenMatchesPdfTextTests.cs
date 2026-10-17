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

    // 글자 모양만 비교한다: 두 렌더의 "잉크" 픽셀 합집합 안에서 채널 차이가 허용치를 넘는 픽셀 비율.
    // (전체 이미지 비율은 빈 종이가 대부분이라 느슨하다.) 화면 쪽을 1pt(=2px) 옮기면 이 비율이 크게 올라 실패해야 한다.
    private const double MaxInkMismatch = 0.60;   // 측정: 정렬 34%, 1pt 이동 86% (Skia와 PDFium의 안티앨리어싱 차이가 정렬 상태의 34%)

    private static readonly string Sample = "Hello 가나다 World\nTest 한글 AVAT";

    private static AppearanceDrawing Build(AppFont font, float dxScreen = 0, float dyScreen = 0)
    {
        AppearanceGeometry.TextFont = font;
        var laid = TextLayout.Layout(font, Sample, 16f, 170f, TextAlign.Left, new PdfPointF(12, 105));
        Assert.Empty(laid.MissingCodepoints);
        var glyphs = laid.Lines.SelectMany(l => l).Select(g => g with { Origin = new PdfPointF(g.Origin.X + dxScreen, g.Origin.Y + dyScreen) }).ToImmutableArray();
        return new AppearanceDrawing([], 1f, laid.Bounds) { Texts = [new TextRunOp("F1", 16f, new PdfColor(20, 20, 20), glyphs)] };
    }

    private async Task<(double Ratio, byte[] Pdfium, byte[] Screen, int W, int H)> CompareAsync(float screenShiftPt, float screenShiftYPt = 0)
    {
        const float W = 200, H = 120;
        using var font = AppFont.Load(FontPath());
        var pdfDrawing = Build(font);
        var screenDrawing = Build(font, screenShiftPt, screenShiftYPt);

        var path = Path.Combine(_dir, "text" + screenShiftPt + "_" + screenShiftYPt + ".pdf");
        using (var doc = new PdfDoc())
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(W);
            page.Height = XUnit.FromPoint(H);
            var fontRef = FontEmbedder.Embed(doc, font, pdfDrawing.CollectFontUse());
            var fonts = new PdfDictionary(doc);
            fonts.Elements["/F1"] = fontRef;
            var res = new PdfDictionary(doc);
            res.Elements["/Font"] = fonts;
            page.Elements["/Resources"] = res;
            var content = new PdfDictionary(doc);
            content.CreateStream(System.Text.Encoding.ASCII.GetBytes(PdfContentWriter.Write(pdfDrawing)));
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
            var cached = AppearanceGeometry.FromDrawing(screenDrawing);
            Assert.NotEmpty(cached.Texts);
            cached.Draw(ctx);
        });

        for (int i = 3; i < pdfium.Length; i += 4) { pdfium[i] = 255; screen[i] = 255; }
        Assert.True(CountInk(pdfium) > 200, "PDFium render is blank");
        Assert.True(CountInk(screen) > 200, "screen render is blank");
        return (InkMismatch(pdfium, screen), pdfium, screen, w, h);
    }

    [Fact]
    public async Task TextRun_ScreenMatchesPdfium()
    {
        var (ratio, pdfium, screen, w, h) = await CompareAsync(0);
        if (ratio > MaxInkMismatch)
        {
            string dump = Path.Combine(Path.GetTempPath(), "scr-text");
            PngFile.WriteBgra(dump + ".pdfium.png", pdfium, w, h);
            PngFile.WriteBgra(dump + ".screen.png", screen, w, h);
            Assert.Fail($"text: ink mismatch {ratio:P2} (allowed {MaxInkMismatch:P1}) ({dump}.*.png)");
        }
    }

    [Fact]
    public async Task TextRun_OnePointShift_IsDetected()
    {
        var (aligned, _, _, _, _) = await CompareAsync(0);
        var (shifted, _, _, _, _) = await CompareAsync(1f);
        Assert.True(shifted > MaxInkMismatch, $"1pt shift not detected: shifted {shifted:P2}, aligned {aligned:P2}, allowed {MaxInkMismatch:P1}");
    }

    [Fact]
    public async Task TextRun_OnePointVerticalShift_IsDetected()
    {
        var (aligned, _, _, _, _) = await CompareAsync(0);
        var (shifted, _, _, _, _) = await CompareAsync(0, 1f);
        Assert.True(shifted > MaxInkMismatch, $"1pt vertical shift not detected: shifted {shifted:P2}, aligned {aligned:P2}, allowed {MaxInkMismatch:P1}");
    }

    [Fact]
    public void TextRun_WithoutTextFont_Throws()
    {
        using var font = AppFont.Load(FontPath());
        var d = Build(font);
        AppearanceGeometry.TextFont = null;
        HeadlessUi.Run(() => Assert.Throws<InvalidOperationException>(() => AppearanceGeometry.FromDrawing(d)));
    }

    [Fact]
    public void Image_WithoutResolver_Throws_AndMissingBitmapIsPlaceholderNotCached()
    {
        var d = new AppearanceDrawing([], 1f, new PdfRectF(0, 0, 10, 10)) { Images = [new ImageOp("Im1", "x", new PdfRectF(0, 0, 10, 10))] };
        try
        {
            AppearanceGeometry.ImageResolver = null;
            HeadlessUi.Run(() => Assert.Throws<InvalidOperationException>(() => AppearanceGeometry.FromDrawing(d)));

            AppearanceGeometry.ImageResolver = _ => null;
            var c = HeadlessUi.Run(() => AppearanceGeometry.FromDrawing(d));
            Assert.False(c.Complete);
            Assert.NotEmpty(c.Items);   // 눈에 보이는 자리 표시 사각형
        }
        finally { AppearanceGeometry.ImageResolver = null; }
    }

    [Fact]
    public void Get_DoesNotCacheIncompleteResult()
    {
        // 글자 없는 주석은 항상 완전하므로 같은 인스턴스 캐시(기존 동작)를 유지한다.
        var a = new Annotation { Kind = AnnotKind.Square, Rect = new PdfRectF(0, 0, 20, 20), Color = new PdfColor(255, 0, 0), BorderWidth = 1 };
        var (c1, c2) = HeadlessUi.Run(() => (AppearanceGeometry.Get(a, 0), AppearanceGeometry.Get(a, 0)));
        Assert.True(c1.Complete);
        Assert.Same(c1, c2);
    }

    [Fact]
    public void Get_IncompleteImageResult_NotCached_UntilResolverProvidesBitmap()
    {
        var a = new Annotation
        {
            Kind = AnnotKind.Stamp, Rect = new PdfRectF(0, 0, 40, 20), Stamp = new StampSpec(StampKind.Image, "", null, "img-1"),
        };
        try
        {
            AppearanceGeometry.ImageResolver = _ => null;
            var (c1, c2) = HeadlessUi.Run(() => (AppearanceGeometry.Get(a, 0), AppearanceGeometry.Get(a, 0)));
            Assert.False(c1.Complete);
            Assert.NotSame(c1, c2);   // 그림이 아직 없으면 캐시하지 않는다

            var bmp = HeadlessUi.Run(() => new Avalonia.Media.Imaging.WriteableBitmap(
                new Avalonia.PixelSize(2, 2), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul));
            AppearanceGeometry.ImageResolver = _ => bmp;
            var (d1, d2) = HeadlessUi.Run(() => (AppearanceGeometry.Get(a, 0), AppearanceGeometry.Get(a, 0)));
            Assert.True(d1.Complete);
            Assert.Same(d1, d2);
        }
        finally { AppearanceGeometry.ImageResolver = null; }
    }

    [Fact]
    public void Get_TextFontSetLater_IsPickedUp_NothingStaleCached()
    {
        var a = new Annotation
        {
            Kind = AnnotKind.FreeText, Intent = FreeTextIntent.TextBox, Rect = new PdfRectF(0, 0, 120, 40), Contents = "Hello 가나다",
            FontSizePt = 12f, BorderWidth = 1f,
        };
        AppearanceGeometry.TextFont = null;
        HeadlessUi.Run(() => Assert.Throws<InvalidOperationException>(() => AppearanceGeometry.Get(a, 0)));

        using var font = AppFont.Load(FontPath());
        AppearanceGeometry.TextFont = font;
        var (c1, c2) = HeadlessUi.Run(() => (AppearanceGeometry.Get(a, 0), AppearanceGeometry.Get(a, 0)));
        Assert.NotEmpty(c1.Texts);
        Assert.Same(c1, c2);
    }

    private static bool IsInk(byte[] b, int i) => b[i] < 200 || b[i + 1] < 200 || b[i + 2] < 200;

    private static int CountInk(byte[] b)
    {
        int n = 0;
        for (int i = 0; i < b.Length; i += 4) if (IsInk(b, i)) n++;
        return n;
    }

    private static double InkMismatch(byte[] a, byte[] b)
    {
        int union = 0, bad = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (!IsInk(a, i) && !IsInk(b, i)) continue;
            union++;
            int worst = 0;
            for (int c = 0; c < 3; c++) worst = Math.Max(worst, Math.Abs(a[i + c] - b[i + c]));
            if (worst > ChannelTolerance) bad++;
        }
        return union == 0 ? 1.0 : (double)bad / union;
    }
}
