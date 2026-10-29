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
/// 다른 뷰어(Acrobat·PDF-XChange·Foxit·Chrome)에서 열어 볼 0.4.0 호환성 샘플 `spikes/out/v040-line-styles-leaders.pdf`
/// (docs/checklists/viewer-compat.md 0.4.0 절). 1쪽: 선 모양 4종 × 굵기, 양쪽 화살표, 폴리라인·다각형 점선. 2쪽: 설명선 지시선 1개·2개(네 방향).
/// 3쪽: /Rotate 90 + 지시선 2개 설명선. 실제 저장 길(PDFium Flush + PDFsharp 패치 단계)로 저장한 뒤 다시 읽어 개수·값을 자동 확인한다.
/// 다른 뷰어에서의 모양은 사람이 확인한다(검증 필요): 특히 둘째 지시선(/SedolCL2)이 보이는지, 점선 간격, 텍스트를 고친 뒤에도 남는지.
/// </summary>
public class V040CompatSampleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-v040compat-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _font = AppFont.Load(FontTestSupport.FontPath());
    private readonly AnnotationAssets _assets = new();

    public V040CompatSampleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        _font.Dispose();
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
            Id = s.Annotations.NewId(), Kind = kind, Name = Guid.NewGuid().ToString("D"), Author = "0.4.0 샘플",
            CreatedAt = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.FromHours(9)),
            ModifiedAt = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.FromHours(9)), Flags = PdfAnnotFlags.Print,
        });
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private static Task Add(DocumentSession s, PageId page, Annotation a) => s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(page, a));

    private static Annotation Callout(DocumentSession s, string text, PdfRectF box, PdfPointF tip, bool two, int rotation = 0)
    {
        var a = Make(s, AnnotKind.FreeText, x => x with
        {
            Intent = FreeTextIntent.Callout, Contents = text, FontSizePt = 12f, TextColor = Black, Color = Red, FillColor = Yellow,
            BorderWidth = 1.5f, TextBox = box, EndEnding = LineEnding.OpenArrow, Rotation = rotation, Subject = two ? "callout-2" : "callout-1",
            Vertices = [tip, AnnotationToolController_LeaderAttach(box, tip)],
        });
        return two ? CalloutLeaders.AddSecond(a) : a;
    }

    private static PdfPointF AnnotationToolController_LeaderAttach(PdfRectF box, PdfPointF tip)
    {
        float cx = (box.Left + box.Right) / 2f, cy = (box.Bottom + box.Top) / 2f;
        if (tip.X < box.Left) return new(box.Left, cy);
        if (tip.X > box.Right) return new(box.Right, cy);
        return tip.Y > box.Top ? new(cx, box.Top) : new(cx, box.Bottom);
    }

    [Fact]
    public async Task CreateLineStylesAndLeadersSample()
    {
        const float W = 595, H = 842;
        var outDir = OutDir();
        Directory.CreateDirectory(outDir);
        var target = Path.Combine(outDir, "v040-line-styles-leaders.pdf");
        string blank = Path.Combine(_dir, "blank.pdf");
        TestPdfFactory.CreateBlank(blank, 3, W, H);

        using var patcher = new PdfSharpAnnotationPatcher(_font);
        var svc = new SaveService([], new PdfAppearanceSource(_font, _assets), patcher, _assets);

        await using (var s = await DocumentSession.OpenAsync(_t, blank, null))
        {
            await s.AnnotationsReady;
            var p1 = s.Pages[0].Id; var p2 = s.Pages[1].Id; var p3 = s.Pages[2].Id;
            await s.Commands.ExecuteAsync(new RotatePagesCommand([p3], 1));

            // ---- 1쪽: 선 모양 4종 × 굵기 1·2·4, 양쪽 화살표, 폴리라인·다각형 ----
            LineStyle[] styles = [LineStyle.Solid, LineStyle.Dotted, LineStyle.Dashed, LineStyle.DashDot];
            float[] widths = [1f, 2f, 4f];
            float y = 800;
            foreach (var st in styles)
                foreach (var w in widths)
                {
                    float yy = y;
                    await Add(s, p1, Make(s, AnnotKind.Line, a => a with
                    {
                        Color = Red, BorderWidth = w, LineStyle = st, Contents = $"{st} {w}pt",
                        Vertices = [new PdfPointF(60, yy), new PdfPointF(300, yy)],
                    }));
                    y -= 22;
                }
            // 양쪽 화살표(닫힌·열린)와 끝 모양 조합, 점선 + 화살표
            await Add(s, p1, Make(s, AnnotKind.Line, a => a with
            {
                Color = Blue, BorderWidth = 2.5f, StartEnding = LineEnding.ClosedArrow, EndEnding = LineEnding.ClosedArrow, Contents = "양쪽 닫힌 화살표",
                Vertices = [new PdfPointF(340, 790), new PdfPointF(540, 790)],
            }));
            await Add(s, p1, Make(s, AnnotKind.Line, a => a with
            {
                Color = Blue, BorderWidth = 2.5f, StartEnding = LineEnding.OpenArrow, EndEnding = LineEnding.OpenArrow, Contents = "양쪽 열린 화살표",
                Vertices = [new PdfPointF(340, 760), new PdfPointF(540, 760)],
            }));
            await Add(s, p1, Make(s, AnnotKind.Line, a => a with
            {
                Color = Green, BorderWidth = 2f, LineStyle = LineStyle.Dashed, StartEnding = LineEnding.Circle, EndEnding = LineEnding.Diamond, Contents = "파선 + 원/마름모",
                Vertices = [new PdfPointF(340, 730), new PdfPointF(540, 730)],
            }));
            await Add(s, p1, Make(s, AnnotKind.Line, a => a with
            {
                Color = Red, BorderWidth = 3f, LineStyle = LineStyle.DashDot, StartEnding = LineEnding.ClosedArrow, EndEnding = LineEnding.Square, Contents = "일점쇄선 + 화살/사각",
                Vertices = [new PdfPointF(340, 700), new PdfPointF(540, 640)],
            }));
            await Add(s, p1, Make(s, AnnotKind.PolyLine, a => a with
            {
                Color = Green, BorderWidth = 2.5f, LineStyle = LineStyle.DashDot, StartEnding = LineEnding.Diamond, EndEnding = LineEnding.OpenArrow, Contents = "폴리라인 일점쇄선",
                Vertices = [new PdfPointF(340, 600), new PdfPointF(400, 520), new PdfPointF(460, 590), new PdfPointF(540, 510)],
            }));
            await Add(s, p1, Make(s, AnnotKind.Polygon, a => a with
            {
                Color = Red, FillColor = Yellow, BorderWidth = 2, LineStyle = LineStyle.Dashed, Opacity = 0.8f, Contents = "다각형 파선",
                Vertices = [new PdfPointF(60, 440), new PdfPointF(220, 480), new PdfPointF(280, 400), new PdfPointF(120, 370)],
            }));
            await Add(s, p1, Make(s, AnnotKind.Polygon, a => a with
            {
                Color = Blue, BorderWidth = 3, LineStyle = LineStyle.Dotted, Contents = "다각형 점선",
                Vertices = [new PdfPointF(340, 450), new PdfPointF(520, 460), new PdfPointF(430, 360)],
            }));

            // ---- 2쪽: 설명선 지시선 1개·2개 (네 방향) ----
            await Add(s, p2, Callout(s, "지시선 1개", new PdfRectF(220, 740, 380, 790), new PdfPointF(60, 760), two: false));
            await Add(s, p2, Callout(s, "지시선 2개: 왼쪽 + 오른쪽", new PdfRectF(220, 620, 380, 670), new PdfPointF(60, 640), two: true));
            await Add(s, p2, Callout(s, "지시선 2개: 오른쪽 + 왼쪽", new PdfRectF(220, 500, 380, 550), new PdfPointF(520, 520), two: true));
            await Add(s, p2, Callout(s, "지시선 2개: 위 + 아래", new PdfRectF(220, 360, 380, 410), new PdfPointF(300, 470), two: true));
            await Add(s, p2, Callout(s, "지시선 2개: 아래 + 위", new PdfRectF(220, 220, 380, 270), new PdfPointF(300, 120), two: true));

            // ---- 3쪽: /Rotate 90 + 지시선 2개 설명선 ----
            await Add(s, p3, Callout(s, "회전한 쪽의 지시선 2개", new PdfRectF(200, 500, 400, 560), new PdfPointF(60, 530), two: true, rotation: 90));

            await svc.SaveAsync(s, target);
        }

        // ---- 다시 열어 확인 ----
        var back = AnnotationReader.Read(File.ReadAllBytes(target), null, [0, 1, 2]);
        Assert.Equal(12 + 2 + 2 + 1 + 2, back[0].Count);
        Assert.All(back[0], o => Assert.Equal(ReadOnlyReason.None, o.Reason));
        Assert.Equal(3, back[0].Count(o => o.Data is { Kind: AnnotKind.Line, LineStyle: LineStyle.Dotted }));
        Assert.Equal(4, back[0].Count(o => o.Data is { Kind: AnnotKind.Line, LineStyle: LineStyle.Dashed }));
        Assert.Equal(4, back[0].Count(o => o.Data is { Kind: AnnotKind.Line, LineStyle: LineStyle.DashDot }));
        Assert.Contains(back[0], o => o.Data is { Kind: AnnotKind.PolyLine, LineStyle: LineStyle.DashDot });
        Assert.Equal(2, back[0].Count(o => o.Data is { Kind: AnnotKind.Polygon } && o.Data.LineStyle != LineStyle.Solid));
        Assert.Contains(back[0], o => o.Data is { Kind: AnnotKind.Line, StartEnding: LineEnding.ClosedArrow, EndEnding: LineEnding.ClosedArrow });

        Assert.Equal(5, back[1].Count(o => o.Data?.Intent == FreeTextIntent.Callout));
        Assert.Equal(1, back[1].Count(o => o.Data is { } d && CalloutLeaders.Count(d) == 1));
        Assert.Equal(4, back[1].Count(o => o.Data is { } d && CalloutLeaders.Count(d) == 2));
        Assert.All(back[1], o => Assert.Equal(ReadOnlyReason.None, o.Reason));
        Assert.Equal(2, CalloutLeaders.Count(back[2].Single(o => o.Data?.Intent == FreeTextIntent.Callout).Data!));

        // PDFium으로 그려 화면 확인용 PNG를 남긴다.
        await _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(_t, target);
            for (int i = 0; i < doc.PageCount; i++)
            {
                using var page = doc.LoadPage(i);
                var size = new PageTransform(page.BoundsPt, page.Rotation, 1.0).ViewSize;
                int pw = (int)Math.Ceiling(size.W), ph = (int)Math.Ceiling(size.H);
                PngFile.WriteBgra(Path.Combine(outDir, $"v040-line-styles-leaders-p{i + 1}.png"), page.RenderTile(1.0, 0, 0, pw, ph, withAnnotations: true), pw, ph);
            }
            return 0;
        });
        Assert.True(File.Exists(target));
    }
}
