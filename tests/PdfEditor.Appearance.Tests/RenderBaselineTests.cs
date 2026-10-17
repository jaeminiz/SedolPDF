using System.Drawing;
using System.Runtime.CompilerServices;
using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.Appearance.Tests;

/// <summary>
/// 주석 종류별로 Flush → 저장 → 다시 열기 → PDFium 렌더 결과를 기준 이미지와 비교한다 (설계서 8.3).
/// 기준 이미지를 다시 만들려면 환경 변수 PDFEDITOR_UPDATE_BASELINE=1 로 실행하고, 만든 그림을 눈으로 확인한 뒤 커밋한다.
/// 검증 필요: 기준 이미지는 PDFium 157 / Windows 렌더 기준이다. 다른 PDFium 버전에서는 안티앨리어싱이 달라질 수 있다.
/// </summary>
public class RenderBaselineTests : IDisposable
{
    private const double Scale = 2;
    private const int ChannelTolerance = 16;
    private const double MaxMismatch = 0.005;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-render-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public RenderBaselineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string BaselineDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "fixtures", "baseline"));

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

    /// <summary>쪽 0에 (회전 후) 주석을 더하고 저장 → 다시 열어 쪽 0을 scale로 그린다.</summary>
    private async Task<(byte[] Bgra, int W, int H, int Rotation)> RenderAsync(
        string pdf, Func<DocumentSession, RectangleF, Annotation[]> make, int rotation, string outName, double scale)
    {
        var target = Path.Combine(_dir, outName);
        await using (var s = await DocumentSession.OpenAsync(_t, pdf, null))
        {
            await s.AnnotationsReady;
            var id = s.Pages[0].Id;
            if (rotation != 0)
                await _t.InvokeAsync(() => s.GetLoadedPage(id).Rotation = rotation);
            var bounds = await _t.InvokeAsync(() => s.GetLoadedPage(id).BoundsPt);
            foreach (var a in make(s, bounds))
                await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, a));
            await new SaveService([], new PdfAppearanceSource(), new PdfEditor.Appearance.Patching.PdfSharpAnnotationPatcher(() => [])).SaveAsync(s, target);
        }
        return await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, target);
            using var page = doc.LoadPage(0);
            int rot = page.Rotation;
            var size = new PageTransform(page.BoundsPt, rot, scale).ViewSize;
            int w = (int)Math.Ceiling(size.W), h = (int)Math.Ceiling(size.H);
            return (page.RenderTile(scale, 0, 0, w, h, withAnnotations: true), w, h, rot);
        });
    }

    private string Blank200(string name, float h = 200)
    {
        var p = Path.Combine(_dir, name);
        TestPdfFactory.CreateBlank(p, 1, 200, h);
        return p;
    }

    private static bool IsLight(byte[] b, int w, int x, int y)
        => b[(y * w + x) * 4] > 230 && b[(y * w + x) * 4 + 1] > 230 && b[(y * w + x) * 4 + 2] > 230;

    private static bool IsYellow(byte[] b, int w, int x, int y)
        => b[(y * w + x) * 4 + 2] > 200 && b[(y * w + x) * 4 + 1] > 180 && b[(y * w + x) * 4] < 100;

    private static void CompareToBaseline(string name, byte[] bgra, int w, int h)
    {
        var dir = BaselineDir();
        Directory.CreateDirectory(dir);
        var baseline = Path.Combine(dir, name + ".png");
        var actual = Path.Combine(dir, name + ".actual.png");
        if (Environment.GetEnvironmentVariable("PDFEDITOR_UPDATE_BASELINE") == "1")
        {
            PngFile.WriteBgra(baseline, bgra, w, h);
            return;
        }
        if (!File.Exists(baseline))
        {
            PngFile.WriteBgra(actual, bgra, w, h);
            Assert.Fail($"기준 이미지 없음: 눈으로 확인한 뒤 .actual을 지우고 이름을 바꿔 커밋 ({actual})");
        }
        var (exp, ew, eh) = PngFile.ReadBgra(baseline);
        Assert.Equal((ew, eh), (w, h));
        var (ratio, max) = ImageCompare.Compare(exp, bgra, w, h, ChannelTolerance);
        if (ratio > MaxMismatch)
        {
            PngFile.WriteBgra(actual, bgra, w, h);
            Assert.Fail($"{name}: mismatch {ratio:P2} (allowed {MaxMismatch:P1}), max channel diff {max} ({actual})");
        }
        else if (File.Exists(actual)) File.Delete(actual);
    }

    [Theory]
    [InlineData("ink")] [InlineData("highlight")] [InlineData("underline")] [InlineData("strikeout")]
    [InlineData("text")] [InlineData("square_fill")] [InlineData("circle")] [InlineData("highlight_area")]
    public async Task Baseline_PerKind(string name)
    {
        var r = await RenderAsync(Blank200(name + ".src.pdf"), (s, _) => [Representative(s, name)], 0, name + ".pdf", Scale);
        CompareToBaseline(name, r.Bgra, r.W, r.H);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task Baseline_TextIcon_Rotated(int rotation)
    {
        // Off-centre icon (upper left of a 200x300 page) so rotations 1-3 move it to the matching corner.
        var name = "text_rot" + rotation;
        var rect = new PdfRectF(20, 260, 40, 280);
        var r = await RenderAsync(Blank200(name + ".src.pdf", 300), (s, _) => [Representative(s, "text") with { Rect = rect }],
            rotation, name + ".pdf", Scale);
        Assert.Equal(rotation, r.Rotation);
        Assert.Equal(rotation is 1 or 3 ? (600, 400) : (400, 600), (r.W, r.H));

        var tr = new PageTransform(RectangleF.FromLTRB(0, 0, 200, 300), rotation, Scale);
        // 3 pt right of the icon's left edge at mid-height: inside the yellow body, left of the text lines.
        var body = tr.ToView(23, 270);
        Assert.True(IsYellow(r.Bgra, r.W, (int)body.X, (int)body.Y), $"icon body not at expected view position ({body.X},{body.Y})");
        var far = tr.ToView(150, 40);
        Assert.True(IsLight(r.Bgra, r.W, (int)far.X, (int)far.Y), "far pixel not light");
        CompareToBaseline(name, r.Bgra, r.W, r.H);
    }

    [Fact]
    public async Task RotatedCropBoxPage_InkLandsWhereDrawn()
    {
        var src = Path.Combine(_dir, "crop.src.pdf");
        TestPdfFactory.CreateWithCropBox(src, (100, 100, 400, 500));
        const double scale = 1.5;
        ViewPoint[] screen = [new(50, 60), new(200, 120)];

        var r = await RenderAsync(src, (s, bounds) =>
        {
            var tr = new PageTransform(bounds, 1, scale);
            var pts = screen.Select(v => { var (x, y) = tr.ToPage(v); return new PdfPointF((float)x, (float)y); }).ToArray();
            var rect = PdfRectF.FromPoints(pts).Inflate(4);
            return [Make(s, AnnotKind.Ink, rect, a => a with
            {
                Color = new PdfColor(0, 0, 0), BorderWidth = 6, InkStrokes = [[pts[0], pts[1]]],
            })];
        }, 1, "crop.pdf", scale);

        Assert.Equal(1, r.Rotation);
        foreach (var v in screen)
        {
            bool dark = false;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = (int)Math.Round(v.X) + dx, y = (int)Math.Round(v.Y) + dy;
                    int o = (y * r.W + x) * 4;
                    if (r.Bgra[o] < 100 && r.Bgra[o + 1] < 100 && r.Bgra[o + 2] < 100) dark = true;
                }
            Assert.True(dark, $"no dark pixel near screen point ({v.X},{v.Y})");
        }
        // Mid-segment pixel is dark too; a pixel well off the stroke is light (negative control).
        int mx = (int)Math.Round((screen[0].X + screen[1].X) / 2), my = (int)Math.Round((screen[0].Y + screen[1].Y) / 2);
        Assert.True(r.Bgra[(my * r.W + mx) * 4] < 100, "mid-segment pixel not dark");
        Assert.True(IsLight(r.Bgra, r.W, mx, my + 60), "negative control pixel not light");
    }
}
