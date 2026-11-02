using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Appearance.Tests.Fonts;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Appearance.Tests;

/// <summary>0.5.0 선 종류·화살촉: Painter 출력 비교(화면과 AP가 같은 코드), 이중선 렌더, 설명선 지시선 왕복.</summary>
public class V050LineStyleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-v50p-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _font = AppFont.Load(FontTestSupport.FontPath());
    private readonly PdfAppearanceSource _ap;
    private readonly PdfSharpAnnotationPatcher _patcher;

    public V050LineStyleTests()
    {
        Directory.CreateDirectory(_dir);
        _ap = new PdfAppearanceSource(_font);
        _patcher = new PdfSharpAnnotationPatcher(_font);
    }

    public void Dispose()
    {
        _t.Dispose();
        _patcher.Dispose();
        _font.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);
    private SaveService Svc() => new([], _ap, _patcher);
    private static readonly PdfColor Red = new(229, 55, 45);

    private async Task<DocumentSession> OpenBlankAsync(string name, float w = 400, float h = 300)
    {
        TestPdfFactory.CreateBlank(P(name), 1, w, h);
        var s = await DocumentSession.OpenAsync(_t, P(name), null);
        await s.AnnotationsReady;
        return s;
    }

    private static Annotation LineOf(LineStyle style, float width = 2f, LineEnding start = LineEnding.None, LineEnding end = LineEnding.None, PdfColor? fill = null)
    {
        var a = new Annotation
        {
            Kind = AnnotKind.Line, Color = Red, FillColor = fill, BorderWidth = width, LineStyle = style, StartEnding = start, EndEnding = end,
            Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print,
            Vertices = [new PdfPointF(40, 150), new PdfPointF(360, 150)],
            CreatedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), ModifiedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero),
        };
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private async Task<string> SaveWithAsync(Annotation a, string name)
    {
        await using var s = await OpenBlankAsync(name + "-src.pdf");
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a with { Id = s.Annotations.NewId() }));
        string target = P(name + ".pdf");
        await Svc().SaveAsync(s, target);
        return target;
    }

    private async Task<byte[]> RenderAsync(string pdf, int w, int h) => await _t.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
        using var pg = d.LoadPage(0);
        return pg.RenderTile(1, 0, 0, w, h, withAnnotations: true);
    });

    // ---- 대시 ----

    [Fact]
    public void LongDash_IsLongerThanDash_ScalesWithWidth_AndIsRecognized()
    {
        var dash = LineStyles.DashArray(LineStyle.Dashed, 2f);
        var lg = LineStyles.DashArray(LineStyle.LongDash, 2f);
        Assert.True(lg[0] > dash[0]);
        Assert.Equal(2, lg.Length);
        Assert.Equal(lg[0] * 2f, LineStyles.DashArray(LineStyle.LongDash, 4f)[0], 4);
        Assert.Equal(LineStyle.LongDash, LineStyleExtensions.FromDash(lg.Select(v => (double)v).ToArray(), 2f));
        Assert.Equal(LineStyle.Dashed, LineStyleExtensions.FromDash(dash.Select(v => (double)v).ToArray(), 2f));
        var d = AnnotationPainter.Paint(LineOf(LineStyle.LongDash, 3f), 0);
        Assert.Equal(LineStyles.DashArray(LineStyle.LongDash, 3f), d.Ops[0].Dash.ToArray());
        Assert.Contains("[24 12] 0 d\n", PdfContentWriter.Write(d));
    }

    // ---- 이중선 ----

    [Fact]
    public void Double_PaintsTwoOffsetStrokes_OneThirdWidth_SymmetricAroundCenter()
    {
        var d = AnnotationPainter.Paint(LineOf(LineStyle.Double, 6f), 0);
        Assert.Equal(2, d.Ops.Length);
        Assert.All(d.Ops, op => { Assert.Equal(2f, op.StrokeWidth, 3); Assert.True(op.Dash.IsDefaultOrEmpty); });
        float y0 = ((MoveTo)d.Ops[0].Path[0]).P.Y, y1 = ((MoveTo)d.Ops[1].Path[0]).P.Y;
        Assert.Equal(150f, (y0 + y1) / 2f, 3);
        Assert.Equal(4f, Math.Abs(y0 - y1), 3);   // 중심에서 ±굵기/3 = ±2
        Assert.True(d.Bounds.Top <= 153.01f && d.Bounds.Bottom >= 146.99f);   // 바깥 가장자리 = 굵기/2
        var solid = AnnotationPainter.Paint(LineOf(LineStyle.Solid, 6f), 0);
        Assert.Single(solid.Ops);
    }

    [Fact]
    public void Double_Polygon_FillOnce_ThenTwoClosedStrokes_CloudStaysSolid()
    {
        var poly = new Annotation
        {
            Kind = AnnotKind.Polygon, Color = Red, FillColor = new PdfColor(255, 240, 150), BorderWidth = 6f, LineStyle = LineStyle.Double,
            Vertices = [new PdfPointF(60, 20), new PdfPointF(240, 30), new PdfPointF(180, 90)],
        };
        var d = AnnotationPainter.Paint(poly, 0);
        Assert.Equal(3, d.Ops.Length);
        Assert.NotNull(d.Ops[0].Fill);
        Assert.Null(d.Ops[0].Stroke);
        Assert.All(d.Ops.Skip(1), op => { Assert.Null(op.Fill); Assert.NotNull(op.Stroke); Assert.IsType<ClosePath>(op.Path[^1]); });
        var cloud = AnnotationPainter.Paint(poly with { CloudIntensity = 1f }, 0);
        Assert.Single(cloud.Ops);   // 구름 테두리는 실선
    }

    [Fact]
    public void Double_ZeroLength_BehavesLikeSolid()
    {
        var a = LineOf(LineStyle.Double, 6f) with { Vertices = [new PdfPointF(50, 50), new PdfPointF(50, 50)] };
        Assert.Equal(AnnotationPainter.Paint(a with { LineStyle = LineStyle.Solid }, 0).Ops.Length, AnnotationPainter.Paint(a, 0).Ops.Length);   // 실선과 같게 처리(깨지지 않는다)
    }

    [Fact]
    public async Task Render_DoubleLine_HasTwoBandsWithGap_SolidHasOne()
    {
        const int W = 400, H = 300;
        var solid = await RenderAsync(await SaveWithAsync(LineOf(LineStyle.Solid, 9f), "r-solid"), W, H);
        var dbl = await RenderAsync(await SaveWithAsync(LineOf(LineStyle.Double, 9f), "r-double"), W, H);
        static bool Ink(byte[] b, int x, int y) { int i = (y * W + x) * 4; return b[i + 2] > 150 && b[i + 1] < 120; }
        int x0 = 200, yc = H - 150;
        int Bands(byte[] b)
        {
            int n = 0; bool on = false;
            for (int y = yc - 8; y <= yc + 8; y++) { bool k = Ink(b, x0, y); if (k && !on) n++; on = k; }
            return n;
        }
        Assert.Equal(1, Bands(solid));
        Assert.Equal(2, Bands(dbl));
        Assert.False(Ink(dbl, x0, yc));   // 가운데는 비어 있다
    }

    // ---- 화살촉 8종 ----

    [Theory]
    [InlineData(LineEnding.OpenArrow)]
    [InlineData(LineEnding.HollowArrow)]
    [InlineData(LineEnding.ClosedArrow)]
    [InlineData(LineEnding.Diamond)]
    [InlineData(LineEnding.Circle)]
    [InlineData(LineEnding.Square)]
    [InlineData(LineEnding.Slash)]
    [InlineData(LineEnding.Butt)]
    public void EachHead_PaintsAShapeAtTheTip_InsideBounds(LineEnding e)
    {
        var d = AnnotationPainter.Paint(LineOf(LineStyle.Solid, 2f, LineEnding.None, e), 0);
        Assert.Equal(2, d.Ops.Length);   // 본선 + 화살촉
        var head = d.Ops[1];
        Assert.Contains(head.Path, s => s is MoveTo or LineTo);
        Assert.True(d.Bounds.Right >= 360f - 0.01f);
        // 본선 끝은 닫힌 촉일 때만 안쪽으로 줄어든다
        float shaftEnd = ((LineTo)d.Ops[0].Path[^1]).P.X;
        if (e is LineEnding.ClosedArrow or LineEnding.HollowArrow or LineEnding.Circle or LineEnding.Square or LineEnding.Diamond) Assert.True(shaftEnd < 360f);
        else Assert.Equal(360f, shaftEnd, 3);
    }

    [Fact]
    public void HollowArrow_NoFill_UnlessInteriorColor_ClosedArrow_FillsWithLineColor()
    {
        var hollow = AnnotationPainter.Paint(LineOf(LineStyle.Solid, 2f, LineEnding.None, LineEnding.HollowArrow), 0).Ops[1];
        var filled = AnnotationPainter.Paint(LineOf(LineStyle.Solid, 2f, LineEnding.None, LineEnding.ClosedArrow), 0).Ops[1];
        Assert.Null(hollow.Fill);
        Assert.Equal(Red, filled.Fill);
        Assert.True(hollow.Path.SequenceEqual(filled.Path));   // 모양은 같고 채움만 다르다
        var withIc = AnnotationPainter.Paint(LineOf(LineStyle.Solid, 2f, LineEnding.None, LineEnding.HollowArrow, new PdfColor(0, 128, 0)), 0).Ops[1];
        Assert.Equal(new PdfColor(0, 128, 0), withIc.Fill);
    }

    [Fact]
    public void HollowArrow_PdfNameIsClosedArrow()
    {
        Assert.Equal("ClosedArrow", LineEnding.HollowArrow.ToPdfName());
        Assert.Equal(LineEnding.HollowArrow, LineEndingExtensions.WithSedolMark(LineEnding.ClosedArrow, "HollowArrow"));
        Assert.Equal(LineEnding.ClosedArrow, LineEndingExtensions.WithSedolMark(LineEnding.ClosedArrow, "None"));
        Assert.Equal(LineEnding.Circle, LineEndingExtensions.WithSedolMark(LineEnding.Circle, "HollowArrow"));
    }

    // ---- 설명선 지시선 ----

    private static Annotation Callout(DocumentSession? s, LineStyle style, LineEnding end)
    {
        var a = new Annotation
        {
            Id = s?.Annotations.NewId() ?? default, Kind = AnnotKind.FreeText, Intent = FreeTextIntent.Callout, TextBox = new PdfRectF(150, 100, 250, 160),
            Vertices = [new PdfPointF(40, 130), new PdfPointF(150, 130)], EndEnding = end, LineStyle = style,
            Contents = "지시선", FontSizePt = 12f, TextColor = new PdfColor(0, 0, 0), Color = Red, FillColor = new PdfColor(255, 245, 157), BorderWidth = 4f,
            Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print, Author = "tester",
            CreatedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), ModifiedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero),
        };
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    [Theory]
    [InlineData(LineStyle.Dotted, LineEnding.Circle)]
    [InlineData(LineStyle.LongDash, LineEnding.HollowArrow)]
    [InlineData(LineStyle.Double, LineEnding.ClosedArrow)]
    [InlineData(LineStyle.Solid, LineEnding.HollowArrow)]
    public async Task Callout_Leader_StyleAndHead_RoundTrip(LineStyle style, LineEnding end)
    {
        var a = Callout(null, style, end);
        // Painter: 지시선만 모양을 따르고 상자 테두리는 실선이다
        var painted = AnnotationPainter.Paint(a, 0, new PaintContext(_font, AnnotationAssets.Empty));
        if (style == LineStyle.Double) Assert.True(painted.Ops.Count(op => op.StrokeWidth > 1.3f && op.StrokeWidth < 1.4f) >= 2);
        else if (style != LineStyle.Solid) Assert.Contains(painted.Ops, op => !op.Dash.IsDefaultOrEmpty);

        string target = await SaveWithAsync(a, "callout-" + style + "-" + end);
        var o = Assert.Single(AnnotationReader.Read(File.ReadAllBytes(target), null, [0])[0]);
        Assert.Equal(ReadOnlyReason.None, o.Reason);
        Assert.Equal(style, o.Data!.LineStyle);
        Assert.Equal(end, o.Data.EndEnding);
        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var annots = (PdfArray)((doc.Pages[0].Elements["/Annots"] as PdfReference)?.Value ?? doc.Pages[0].Elements["/Annots"]!);
        var d = (PdfDictionary)((annots.Elements[0] as PdfReference)?.Value ?? annots.Elements[0]);
        Assert.False(d.Elements.ContainsKey("/BS"));   // 상자 테두리에는 대시를 쓰지 않는다
        Assert.Equal(style != LineStyle.Solid, d.Elements.ContainsKey("/SedolLS"));
        Assert.Equal(end == LineEnding.HollowArrow, d.Elements.ContainsKey("/SedolLE"));
        Assert.Equal(end.ToPdfName(), d.Elements.GetName("/LE").TrimStart('/'));
    }

    [Fact]
    public async Task Callout_ChangedToSolidAndFilled_RemovesSedolKeys()
    {
        string first = await SaveWithAsync(Callout(null, LineStyle.Double, LineEnding.HollowArrow), "cs1");
        await using var s = await DocumentSession.OpenAsync(_t, first, null);
        await s.AnnotationsReady;
        var id = s.Pages[0].Id;
        var before = Assert.Single(s.Annotations.Get(id).Items).Data;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(before, before with { LineStyle = LineStyle.Solid, EndEnding = LineEnding.ClosedArrow })]));
        await Svc().SaveAsync(s, P("cs2.pdf"));
        using var doc = PdfReader.Open(P("cs2.pdf"), PdfDocumentOpenMode.Import);
        var annots = (PdfArray)((doc.Pages[0].Elements["/Annots"] as PdfReference)?.Value ?? doc.Pages[0].Elements["/Annots"]!);
        var d = (PdfDictionary)((annots.Elements[0] as PdfReference)?.Value ?? annots.Elements[0]);
        Assert.False(d.Elements.ContainsKey("/SedolLS"));
        Assert.False(d.Elements.ContainsKey("/SedolLE"));
    }
}
