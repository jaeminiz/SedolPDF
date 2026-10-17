using System.Drawing;
using Pdf.Native;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Appearance;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Viewer.Annotations;

/// <summary>
/// 설계서 4.2-3: 화면 출력(AnnotationOverlay)과 PDF 저장(AP) 결과가 같은 모양인지, Avalonia 오프스크린 비트맵과 PDFium 렌더를 비교한다.
/// 안티앨리어싱 차이는 허용: 채널 차이 40 초과 픽셀이 2% 이하.
/// </summary>
public class ScreenMatchesPdfTests : IDisposable
{
    private const double Scale = 2;
    private const int ChannelTolerance = 40;
    private const double MaxMismatch = 0.02;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-scr-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public ScreenMatchesPdfTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly PdfColor Red = new(229, 55, 45);
    private static readonly PdfColor Yellow = new(255, 235, 0);
    private static readonly PdfColor Blue = new(30, 90, 220);

    private static Annotation Make(DocumentSession s, AnnotKind kind, PdfRectF rect, Func<Annotation, Annotation> f)
        => f(new Annotation
        {
            Id = s.Annotations.NewId(),
            Kind = kind,
            Rect = rect,
            Name = Guid.NewGuid().ToString("D"),
            Flags = PdfAnnotFlags.Print,
        });

    // Task 9(RenderBaselineTests)와 같은 8가지.
    private static Annotation Representative(DocumentSession s, string name) => name switch
    {
        "ink" => Make(s, AnnotKind.Ink, new PdfRectF(30, 40, 170, 150), a => a with
        {
            Color = Red, BorderWidth = 4,
            InkStrokes = [[new PdfPointF(40, 50), new PdfPointF(80, 140), new PdfPointF(120, 60), new PdfPointF(160, 130)]],
        }),
        "highlight" => Make(s, AnnotKind.Highlight, new PdfRectF(30, 90, 170, 110), a => a with
        {
            Color = Yellow, Opacity = 0.5f, Quads = [PdfQuadF.FromRect(new PdfRectF(30, 90, 170, 110))],
        }),
        "underline" => Make(s, AnnotKind.Underline, new PdfRectF(30, 90, 170, 110), a => a with
        {
            Color = Blue, Quads = [PdfQuadF.FromRect(new PdfRectF(30, 90, 170, 110))],
        }),
        "strikeout" => Make(s, AnnotKind.StrikeOut, new PdfRectF(30, 90, 170, 110), a => a with
        {
            Color = Red, Quads = [PdfQuadF.FromRect(new PdfRectF(30, 90, 170, 110))],
        }),
        "text" => Make(s, AnnotKind.Text, new PdfRectF(90, 90, 110, 110), a => a with { Color = Yellow, Contents = "memo" }),
        "square_fill" => Make(s, AnnotKind.Square, new PdfRectF(40, 50, 160, 150), a => a with
        {
            Color = Red, FillColor = Yellow, BorderWidth = 3, Opacity = 0.8f,
        }),
        "circle" => Make(s, AnnotKind.Circle, new PdfRectF(40, 50, 160, 150), a => a with { Color = Blue, BorderWidth = 3 }),
        "highlight_area" => Make(s, AnnotKind.Highlight, new PdfRectF(40, 50, 160, 150), a => a with
        {
            Color = Yellow, Opacity = 0.4f, Quads = [PdfQuadF.FromRect(new PdfRectF(40, 50, 160, 150))],
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private async Task<(byte[] Pdfium, byte[] Screen, int W, int H)> CompareAsync(string name, int rotation)
    {
        string tag = name + "_r" + rotation;
        var pdf = Path.Combine(_dir, tag + ".src.pdf");
        TestPdfFactory.CreateBlank(pdf, 1, 200, 300);
        var target = Path.Combine(_dir, tag + ".pdf");
        PageAnnotations model;
        RectangleF bounds;
        await using (var s = await DocumentSession.OpenAsync(_t, pdf, null))
        {
            await s.AnnotationsReady;
            var id = s.Pages[0].Id;
            if (rotation != 0) await _t.InvokeAsync(() => s.GetLoadedPage(id).Rotation = rotation);
            bounds = await _t.InvokeAsync(() => s.GetLoadedPage(id).BoundsPt);
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, Representative(s, name)));
            await new SaveService([], new PdfAppearanceSource(), new PdfEditor.Appearance.Patching.PdfSharpAnnotationPatcher(() => [])).SaveAsync(s, target);
            model = s.Annotations.Get(id);   // 새 주석 = ModelDrawn
        }
        Assert.Contains(model.Items, i => i.ModelDrawn);

        var (pdfium, w, h) = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, target);
            using var page = doc.LoadPage(0);
            var size = new PageTransform(page.BoundsPt, page.Rotation, Scale).ViewSize;
            int pw = (int)Math.Ceiling(size.W), ph = (int)Math.Ceiling(size.H);
            return (page.RenderTile(Scale, 0, 0, pw, ph, withAnnotations: true), pw, ph);
        });

        var tr = new PageTransform(bounds, rotation, Scale);
        var screen = HeadlessUi.RenderBgra(w, h, ctx =>
            AnnotationOverlay.DrawPage(ctx, model, new Avalonia.Rect(0, 0, w, h), tr, rotation,
                new Dictionary<AnnotId, Annotation?>()));
        return (pdfium, screen, w, h);
    }

    [Theory]
    [InlineData("ink")] [InlineData("highlight")] [InlineData("underline")] [InlineData("strikeout")]
    [InlineData("text")] [InlineData("square_fill")] [InlineData("circle")] [InlineData("highlight_area")]
    public async Task ScreenRender_MatchesPdfiumRender(string name)
    {
        var (pdfium, screen, w, h) = await CompareAsync(name, 0);
        AssertMatches(name, pdfium, screen, w, h);
    }

    [Theory]
    [InlineData("text", 1)] [InlineData("text", 3)] [InlineData("ink", 2)] [InlineData("square_fill", 1)]
    public async Task ScreenRender_MatchesPdfiumRender_Rotated(string name, int rotation)
    {
        var (pdfium, screen, w, h) = await CompareAsync(name, rotation);
        AssertMatches(name + "_r" + rotation, pdfium, screen, w, h);
    }

    private static void AssertMatches(string name, byte[] pdfium, byte[] screen, int w, int h)
    {
        for (int i = 3; i < pdfium.Length; i += 4) { pdfium[i] = 255; screen[i] = 255; }   // 둘 다 불투명 기준
        Assert.True(CountNonWhite(pdfium) > 20, $"{name}: PDFium render is blank");
        Assert.True(CountNonWhite(screen) > 20, $"{name}: screen render is blank");
        var (ratio, max) = ImageCompare.Compare(pdfium, screen, w, h, ChannelTolerance);
        if (ratio > MaxMismatch)
        {
            string dump = Path.Combine(Path.GetTempPath(), $"scr-{name}");
            PngFile.WriteBgra(dump + ".pdfium.png", pdfium, w, h);
            PngFile.WriteBgra(dump + ".screen.png", screen, w, h);
            Assert.Fail($"{name}: mismatch {ratio:P2} (allowed {MaxMismatch:P1}), max channel diff {max} ({dump}.*.png)");
        }
    }

    private static int CountNonWhite(byte[] b)
    {
        int n = 0;
        for (int i = 0; i < b.Length; i += 4)
            if (b[i] < 245 || b[i + 1] < 245 || b[i + 2] < 245) n++;
        return n;
    }
}
