using System.Runtime.CompilerServices;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Appearance.Tests.Fonts;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;

namespace PdfEditor.Appearance.Tests;

/// <summary>
/// 다른 뷰어에서 열어 볼 0.5.0 호환성 샘플 `spikes/out/v050-style-lines.pdf`·`v050-style-text.pdf`(docs/checklists/viewer-compat.md 0.5.0 절).
/// 실제 저장 길(PDFium Flush + PDFsharp 패치 단계)로 저장한 뒤 다시 읽어 개수·값을 자동 확인한다.
/// 다른 뷰어에서의 모양(이중선, 속 빈/채운 화살촉, 굵게·기울임 번짐, 명조)은 사람이 확인한다(검증 필요).
/// </summary>
public class V050CompatSampleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-v050compat-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _sans = AppFont.Load(FontTestSupport.FontPath());
    private readonly AppFont _serif;
    private readonly AnnotationAssets _assets = new();

    public V050CompatSampleTests()
    {
        Directory.CreateDirectory(_dir);
        _serif = AppFont.Load(Path.Combine(Path.GetDirectoryName(FontTestSupport.FontPath())!, "NotoSerifKR-Regular.otf"));
    }

    public void Dispose()
    {
        _t.Dispose();
        _sans.Dispose();
        _serif.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string OutDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "spikes", "out"));

    private static readonly PdfColor Red = new(229, 55, 45);
    private static readonly PdfColor Yellow = new(255, 245, 157);
    private static readonly PdfColor Blue = new(30, 90, 220);
    private static readonly PdfColor Green = new(20, 150, 70);
    private static readonly PdfColor Black = new(0, 0, 0);

    private static Annotation Make(DocumentSession s, AnnotKind kind, Func<Annotation, Annotation> f)
    {
        var a = f(new Annotation
        {
            Id = s.Annotations.NewId(), Kind = kind, Name = Guid.NewGuid().ToString("D"), Author = "0.5.0 샘플",
            CreatedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(9)),
            ModifiedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(9)), Flags = PdfAnnotFlags.Print,
        });
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private static Task Add(DocumentSession s, PageId page, Annotation a) => s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(page, a));

    private static PdfPointF Attach(PdfRectF box, PdfPointF tip)
    {
        float cx = (box.Left + box.Right) / 2f, cy = (box.Bottom + box.Top) / 2f;
        if (tip.X < box.Left) return new(box.Left, cy);
        if (tip.X > box.Right) return new(box.Right, cy);
        return tip.Y > box.Top ? new(cx, box.Top) : new(cx, box.Bottom);
    }

    private static Annotation Callout(DocumentSession s, string text, PdfRectF box, PdfPointF tip, Func<Annotation, Annotation> f)
        => Make(s, AnnotKind.FreeText, a => f(a with
        {
            Intent = FreeTextIntent.Callout, Contents = text, FontSizePt = 12f, TextColor = Black, Color = Red, FillColor = Yellow,
            BorderWidth = 3f, TextBox = box, EndEnding = LineEnding.OpenArrow, Vertices = [tip, Attach(box, tip)],
        }));

    private SaveService Service(out PdfSharpAnnotationPatcher patcher)
    {
        patcher = new PdfSharpAnnotationPatcher(_sans) { SerifFont = () => _serif };
        return new SaveService([], new PdfAppearanceSource(_sans, _assets, _serif), patcher, _assets);
    }

    private async Task WritePreviewsAsync(string target, string stem, string outDir) => await _t.InvokeAsync(() =>
    {
        using var doc = Pdf.Native.PdfDocument.Open(_t, target);
        for (int i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.LoadPage(i);
            var size = new PageTransform(page.BoundsPt, page.Rotation, 1.0).ViewSize;
            int pw = (int)Math.Ceiling(size.W), ph = (int)Math.Ceiling(size.H);
            PngFile.WriteBgra(Path.Combine(outDir, $"{stem}-p{i + 1}.png"), page.RenderTile(1.0, 0, 0, pw, ph, withAnnotations: true), pw, ph);
        }
        return 0;
    });

    [Fact]
    public async Task CreateLineStyleAndArrowheadSample()
    {
        const float W = 595, H = 842;
        var outDir = OutDir();
        Directory.CreateDirectory(outDir);
        var target = Path.Combine(outDir, "v050-style-lines.pdf");
        string blank = Path.Combine(_dir, "blank-lines.pdf");
        TestPdfFactory.CreateBlank(blank, 2, W, H);
        var svc = Service(out var patcher);
        using var _ = patcher;

        LineStyle[] styles = [LineStyle.Solid, LineStyle.Dotted, LineStyle.Dashed, LineStyle.LongDash, LineStyle.DashDot, LineStyle.Double];
        float[] widths = [1f, 2f, 4f, 6f];
        LineEnding[] heads = [LineEnding.OpenArrow, LineEnding.HollowArrow, LineEnding.ClosedArrow, LineEnding.Diamond, LineEnding.Circle, LineEnding.Square, LineEnding.Slash, LineEnding.Butt];

        await using (var s = await DocumentSession.OpenAsync(_t, blank, null))
        {
            await s.AnnotationsReady;
            var p1 = s.Pages[0].Id; var p2 = s.Pages[1].Id;

            float y = 810;
            foreach (var st in styles)
                foreach (var w in widths)
                {
                    float yy = y;
                    await Add(s, p1, Make(s, AnnotKind.Line, a => a with
                    {
                        Color = Red, BorderWidth = w, LineStyle = st, Contents = $"{st} {w}pt", Vertices = [new PdfPointF(50, yy), new PdfPointF(270, yy)],
                    }));
                    y -= 22;
                }
            // 화살촉 8종(끝). 채움색(/IC)이 없으면 채운 화살촉은 선 색으로 차고 속 빈 화살촉은 윤곽만 보인다.
            y = 810;
            foreach (var e in heads)
            {
                float yy = y;
                await Add(s, p1, Make(s, AnnotKind.Line, a => a with
                {
                    Color = Blue, BorderWidth = 2f, EndEnding = e, Contents = $"끝 {e}", Vertices = [new PdfPointF(330, yy), new PdfPointF(540, yy)],
                }));
                y -= 34;
            }
            // 양쪽 조합 + 채움색(/IC)이 있는 경우
            await Add(s, p1, Make(s, AnnotKind.Line, a => a with
            {
                Color = Blue, FillColor = Yellow, BorderWidth = 2.5f, StartEnding = LineEnding.HollowArrow, EndEnding = LineEnding.ClosedArrow, Contents = "IC 노랑: 속 빈 + 채운",
                Vertices = [new PdfPointF(330, 520), new PdfPointF(540, 520)],
            }));
            await Add(s, p1, Make(s, AnnotKind.Line, a => a with
            {
                Color = Green, BorderWidth = 4f, LineStyle = LineStyle.Double, StartEnding = LineEnding.Circle, EndEnding = LineEnding.HollowArrow, Contents = "이중선 + 원/속 빈",
                Vertices = [new PdfPointF(330, 480), new PdfPointF(540, 440)],
            }));
            await Add(s, p1, Make(s, AnnotKind.Line, a => a with
            {
                Color = Red, BorderWidth = 3f, LineStyle = LineStyle.LongDash, StartEnding = LineEnding.Butt, EndEnding = LineEnding.Slash, Contents = "긴 점선 + 막대/빗금",
                Vertices = [new PdfPointF(330, 400), new PdfPointF(540, 380)],
            }));

            // ---- 2쪽: 폴리라인·다각형·설명선 지시선 ----
            await Add(s, p2, Make(s, AnnotKind.PolyLine, a => a with
            {
                Color = Green, BorderWidth = 6f, LineStyle = LineStyle.Double, StartEnding = LineEnding.Diamond, EndEnding = LineEnding.HollowArrow, Contents = "폴리라인 이중선",
                Vertices = [new PdfPointF(60, 800), new PdfPointF(160, 720), new PdfPointF(260, 790), new PdfPointF(380, 700)],
            }));
            await Add(s, p2, Make(s, AnnotKind.PolyLine, a => a with
            {
                Color = Blue, BorderWidth = 3f, LineStyle = LineStyle.LongDash, EndEnding = LineEnding.ClosedArrow, Contents = "폴리라인 긴 점선",
                Vertices = [new PdfPointF(60, 660), new PdfPointF(160, 600), new PdfPointF(300, 650)],
            }));
            await Add(s, p2, Make(s, AnnotKind.Polygon, a => a with
            {
                Color = Red, FillColor = Yellow, BorderWidth = 6f, LineStyle = LineStyle.Double, Contents = "다각형 이중선(채움)",
                Vertices = [new PdfPointF(60, 540), new PdfPointF(220, 580), new PdfPointF(280, 500), new PdfPointF(120, 470)],
            }));
            await Add(s, p2, Make(s, AnnotKind.Polygon, a => a with
            {
                Color = Blue, BorderWidth = 6f, LineStyle = LineStyle.Double, Contents = "다각형 이중선(채움 없음)",
                Vertices = [new PdfPointF(340, 560), new PdfPointF(520, 570), new PdfPointF(430, 470)],
            }));
            await Add(s, p2, Make(s, AnnotKind.Polygon, a => a with
            {
                Color = Green, FillColor = Yellow, BorderWidth = 3f, LineStyle = LineStyle.LongDash, Contents = "다각형 긴 점선",
                Vertices = [new PdfPointF(340, 440), new PdfPointF(520, 450), new PdfPointF(430, 350)],
            }));
            await Add(s, p2, Callout(s, "지시선 이중선 + 속 빈 화살", new PdfRectF(220, 300, 400, 350), new PdfPointF(60, 320),
                a => a with { LineStyle = LineStyle.Double, EndEnding = LineEnding.HollowArrow }));
            await Add(s, p2, Callout(s, "지시선 일점쇄선 + 원", new PdfRectF(220, 220, 400, 270), new PdfPointF(60, 240),
                a => a with { LineStyle = LineStyle.DashDot, EndEnding = LineEnding.Circle, BorderWidth = 2f }));
            await Add(s, p2, Callout(s, "지시선 긴 점선 + 채운 화살", new PdfRectF(220, 140, 400, 190), new PdfPointF(520, 160),
                a => a with { LineStyle = LineStyle.LongDash, EndEnding = LineEnding.ClosedArrow, BorderWidth = 2f }));
            await svc.SaveAsync(s, target);
        }

        // ---- 다시 열어 확인 ----
        var back = AnnotationReader.Read(File.ReadAllBytes(target), null, [0, 1]);
        Assert.Equal(24 + 8 + 3, back[0].Count);
        Assert.All(back[0], o => Assert.Equal(ReadOnlyReason.None, o.Reason));
        Assert.All(back[1], o => Assert.Equal(ReadOnlyReason.None, o.Reason));
        var expected = new Dictionary<LineStyle, int>
        {
            [LineStyle.Solid] = 4 + 8 + 1,   // 굵기 4개 + 화살촉 8종 + 채움색 예제
            [LineStyle.Dotted] = 4, [LineStyle.Dashed] = 4, [LineStyle.DashDot] = 4,
            [LineStyle.LongDash] = 4 + 1, [LineStyle.Double] = 4 + 1,
        };
        foreach (var (st, n) in expected)
            Assert.Equal(n, back[0].Count(o => o.Data is { Kind: AnnotKind.Line } d && d.LineStyle == st));
        foreach (var e in heads)
            Assert.Contains(back[0], o => o.Data is { Kind: AnnotKind.Line } d && d.EndEnding == e && d.Contents == $"끝 {e}");
        Assert.Contains(back[0], o => o.Data is { Kind: AnnotKind.Line, StartEnding: LineEnding.HollowArrow, EndEnding: LineEnding.ClosedArrow, FillColor: not null });
        Assert.Equal(3, back[1].Count(o => o.Data?.Intent == FreeTextIntent.Callout));
        Assert.Contains(back[1], o => o.Data is { Intent: FreeTextIntent.Callout, LineStyle: LineStyle.Double, EndEnding: LineEnding.HollowArrow });
        Assert.Equal(5, back[1].Count(o => o.Data is { Kind: AnnotKind.Polygon or AnnotKind.PolyLine } d && d.LineStyle is LineStyle.Double or LineStyle.LongDash));
        await WritePreviewsAsync(target, "v050-style-lines", outDir);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task CreateTextFormatSample()
    {
        const float W = 595, H = 842;
        var outDir = OutDir();
        Directory.CreateDirectory(outDir);
        var target = Path.Combine(outDir, "v050-style-text.pdf");
        string blank = Path.Combine(_dir, "blank-text.pdf");
        TestPdfFactory.CreateBlank(blank, 2, W, H);
        var svc = Service(out var patcher);
        using var _ = patcher;

        const string Sample = "Hello 가나다 ABC 1234\n굵게·기울임 시험 AVAT";
        (string Name, Func<Annotation, Annotation> F)[] boxes =
        [
            ("보통", a => a),
            ("굵게", a => a with { Bold = true }),
            ("기울임", a => a with { Italic = true }),
            ("밑줄", a => a with { Underline = true }),
            ("취소선", a => a with { StrikeThrough = true }),
            ("명조", a => a with { TextFamily = TextFontFamily.Serif }),
            ("명조 전부", a => a with { TextFamily = TextFontFamily.Serif, Bold = true, Italic = true, Underline = true, StrikeThrough = true }),
            ("고딕 전부", a => a with { Bold = true, Italic = true, Underline = true, StrikeThrough = true }),
        ];

        await using (var s = await DocumentSession.OpenAsync(_t, blank, null))
        {
            await s.AnnotationsReady;
            var p1 = s.Pages[0].Id; var p2 = s.Pages[1].Id;
            await s.Commands.ExecuteAsync(new RotatePagesCommand([p2], 1));

            float y = 810;
            foreach (var (name, f) in boxes)
            {
                float top = y;
                await Add(s, p1, Make(s, AnnotKind.FreeText, a => f(a with
                {
                    Intent = FreeTextIntent.TextBox, Rect = new PdfRectF(40, top - 56, 320, top), Contents = name + ": " + Sample, FontSizePt = 14f,
                    TextColor = Black, Color = Red, FillColor = Yellow, BorderWidth = 1f, Subject = name,
                })));
                y -= 66;
            }
            await Add(s, p1, Make(s, AnnotKind.FreeText, a => a with
            {
                Intent = FreeTextIntent.Typewriter, Rect = new PdfRectF(340, 760, 560, 800), Contents = "타자기 굵게·기울임", FontSizePt = 16f, TextColor = Blue,
                Color = null, FillColor = null, BorderWidth = 0f, Bold = true, Italic = true,
            }));
            await Add(s, p1, Make(s, AnnotKind.FreeText, a => a with
            {
                Intent = FreeTextIntent.Typewriter, Rect = new PdfRectF(340, 700, 560, 740), Contents = "타자기 명조 밑줄", FontSizePt = 16f, TextColor = Green,
                Color = null, FillColor = null, BorderWidth = 0f, TextFamily = TextFontFamily.Serif, Underline = true,
            }));
            await Add(s, p1, Callout(s, "설명선 굵게+밑줄", new PdfRectF(380, 560, 560, 610), new PdfPointF(340, 520), a => a with { Bold = true, Underline = true, BorderWidth = 1.5f }));
            await Add(s, p1, Callout(s, "설명선 명조+기울임", new PdfRectF(380, 460, 560, 510), new PdfPointF(340, 420), a => a with { TextFamily = TextFontFamily.Serif, Italic = true, BorderWidth = 1.5f }));
            // 글자 크기 대비: 굵게 번짐이 글자 크기에 비례하는지
            foreach (var (size, off) in new[] { (9f, 0f), (14f, 40f), (24f, 90f), (36f, 160f) })
            {
                float yy = 330 - off;
                await Add(s, p1, Make(s, AnnotKind.FreeText, a => a with
                {
                    Intent = FreeTextIntent.TextBox, Rect = new PdfRectF(340, yy - 50, 560, yy), Contents = $"{size}pt 굵게 AVA", FontSizePt = size, TextColor = Black,
                    Color = null, FillColor = null, BorderWidth = 0f, Bold = true,
                }));
            }

            // ---- 2쪽: /Rotate 90 ----
            await Add(s, p2, Make(s, AnnotKind.FreeText, a => a with
            {
                Intent = FreeTextIntent.TextBox, Rect = new PdfRectF(60, 600, 340, 680), Contents = "회전한 쪽 굵게+기울임+밑줄\nsecond line", FontSizePt = 14f, TextColor = Black,
                Color = Red, FillColor = Yellow, BorderWidth = 1f, Bold = true, Italic = true, Underline = true, Rotation = 90,
            }));
            await Add(s, p2, Callout(s, "회전한 쪽 명조 설명선", new PdfRectF(200, 400, 400, 460), new PdfPointF(60, 430),
                a => a with { TextFamily = TextFontFamily.Serif, StrikeThrough = true, Rotation = 90 }));
            await svc.SaveAsync(s, target);
        }

        var back = AnnotationReader.Read(File.ReadAllBytes(target), null, [0, 1]);
        Assert.All(back[0], o => Assert.Equal(ReadOnlyReason.None, o.Reason));
        Assert.All(back[1], o => Assert.Equal(ReadOnlyReason.None, o.Reason));
        Assert.Equal(16, back[0].Count);
        Assert.Equal(2, back[1].Count);
        Assert.Equal(14, back[0].Count(o => o.Data is { Kind: AnnotKind.FreeText } d && d.Intent != FreeTextIntent.Callout));
        Assert.Equal(9, back[0].Count(o => o.Data is { Bold: true }));
        Assert.Equal(4, back[0].Count(o => o.Data is { TextFamily: TextFontFamily.Serif }));
        Assert.Equal(2, back[0].Count(o => o.Data is { Bold: true, Italic: true, Underline: true, StrikeThrough: true }));
        Assert.True(back[1].All(o => o.Data!.Rotation == 90));
        Assert.Contains(back[1], o => o.Data is { Bold: true, Italic: true, Underline: true });

        await WritePreviewsAsync(target, "v050-style-text", outDir);
        Assert.True(File.Exists(target));
    }
}
