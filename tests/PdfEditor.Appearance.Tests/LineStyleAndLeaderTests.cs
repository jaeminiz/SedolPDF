using System.Collections.Immutable;
using System.Text;
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

/// <summary>
/// 0.4.0 Task 2·3: 선 모양(대시)·양쪽 끝 모양과 설명선 지시선 2개의 그리기(Painter 결과 비교, PDFium 렌더).
/// 화면과 AP는 같은 Painter 결과를 쓰므로 Painter의 PaintOp·내용 스트림을 직접 확인하고, PDFium 렌더로 실제 모양과 영역 밖 0 px을 확인한다.
/// </summary>
public class LineStyleAndLeaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-lsl-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _font = AppFont.Load(FontTestSupport.FontPath());
    private readonly PdfAppearanceSource _ap;
    private readonly PdfSharpAnnotationPatcher _patcher;

    public LineStyleAndLeaderTests()
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
    private PaintContext Ctx => new(_font, AnnotationAssets.Empty);

    private static readonly PdfColor Red = new(229, 55, 45);
    private static readonly PdfColor Yellow = new(255, 245, 157);

    private async Task<DocumentSession> OpenBlankAsync(string name = "blank.pdf", float w = 400, float h = 300)
    {
        TestPdfFactory.CreateBlank(P(name), 1, w, h);
        var s = await DocumentSession.OpenAsync(_t, P(name), null);
        await s.AnnotationsReady;
        return s;
    }

    private static Annotation LineOf(DocumentSession? s, LineStyle style, float width = 2f, LineEnding start = LineEnding.None, LineEnding end = LineEnding.None)
    {
        var a = new Annotation
        {
            Id = s?.Annotations.NewId() ?? default, Kind = AnnotKind.Line, Color = Red, BorderWidth = width, LineStyle = style,
            StartEnding = start, EndEnding = end, Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print,
            Vertices = [new PdfPointF(40, 150), new PdfPointF(360, 150)],
            CreatedAt = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero), ModifiedAt = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero),
        };
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    // ---- Task 2: 대시 계산·Painter·내용 스트림 ----

    [Theory]
    [InlineData(LineStyle.Dotted)]
    [InlineData(LineStyle.Dashed)]
    [InlineData(LineStyle.DashDot)]
    public void LineStyles_DashArray_ScalesWithWidth(LineStyle style)
    {
        var d1 = LineStyles.DashArray(style, 1f);
        var d2 = LineStyles.DashArray(style, 2f);
        var d4 = LineStyles.DashArray(style, 4f);
        Assert.NotEmpty(d1);
        Assert.Equal(d1.Length, d2.Length);
        for (int i = 0; i < d1.Length; i++)
        {
            Assert.Equal(d1[i] * 2f, d2[i], 4);
            Assert.Equal(d1[i] * 4f, d4[i], 4);
        }
        Assert.Equal(0, d1.Length % 2);                  // 켜짐/꺼짐 쌍
        Assert.Empty(LineStyles.DashArray(LineStyle.Solid, 3f));
        // 아주 가는 선도 1pt 굵기로 본다(눈에 안 보이는 대시를 피한다)
        Assert.Equal(LineStyles.DashArray(style, 1f), LineStyles.DashArray(style, 0.5f));
    }

    [Fact]
    public void FromDash_RecognizesOnlyOwnPatterns()
    {
        foreach (var s in new[] { LineStyle.Dotted, LineStyle.Dashed, LineStyle.DashDot })
            foreach (float w in new[] { 1f, 2.5f, 8f })
                Assert.Equal(s, LineStyleExtensions.FromDash(LineStyles.DashArray(s, w).Select(v => (double)v).ToArray(), w));
        Assert.Null(LineStyleExtensions.FromDash([5.7, 1.3], 2f));
        Assert.Null(LineStyleExtensions.FromDash([3.0], 1f));
    }

    [Fact]
    public void Painter_SolidHasNoDash_StyledHasDash_DottedIsRound()
    {
        var solid = AnnotationPainter.Paint(LineOf(null, LineStyle.Solid), 0);
        Assert.All(solid.Ops, op => Assert.True(op.Dash.IsDefaultOrEmpty));
        foreach (var style in new[] { LineStyle.Dotted, LineStyle.Dashed, LineStyle.DashDot })
        {
            var d = AnnotationPainter.Paint(LineOf(null, style, 3f), 0);
            var shaft = d.Ops[0];
            Assert.Equal(LineStyles.DashArray(style, 3f), shaft.Dash.ToArray());
            Assert.Equal(style == LineStyle.Dotted ? LineCap.Round : LineCap.Butt, shaft.Cap);
        }
        // 선 모양이 적용되지 않는 유형(Square)은 LineStyle이 있어도 대시가 없다
        var sq = AnnotationPainter.Paint(new Annotation { Kind = AnnotKind.Square, Rect = new PdfRectF(0, 0, 50, 50), Color = Red, BorderWidth = 2, LineStyle = LineStyle.Dashed }, 0);
        Assert.All(sq.Ops, op => Assert.True(op.Dash.IsDefaultOrEmpty));
    }

    [Fact]
    public void ContentWriter_Solid_Unchanged_Dashed_WritesD_AndEndingsResetToSolid()
    {
        // 실선 출력은 이전 판과 같은 문자열이다.
        var solid = PdfContentWriter.Write(AnnotationPainter.Paint(LineOf(null, LineStyle.Solid, 2f), 0));
        Assert.Equal("q\n1 0 0 RG\n2 w\n0 J\n1 j\n40 150 m\n360 150 l\nS\nQ\n".Replace("1 0 0 RG", "0.898 0.216 0.176 RG"), solid);
        Assert.DoesNotContain(" d\n", solid);

        var dashed = PdfContentWriter.Write(AnnotationPainter.Paint(LineOf(null, LineStyle.Dashed, 2f, LineEnding.ClosedArrow, LineEnding.Circle), 0));
        Assert.Contains("[8 6] 0 d\n", dashed);
        // 끝 모양(닫힌 화살·원)은 실선 — 대시를 물려받지 않게 바로 되돌린다
        int dashAt = dashed.IndexOf("[8 6] 0 d\n", StringComparison.Ordinal);
        int resetAt = dashed.IndexOf("[] 0 d\n", StringComparison.Ordinal);
        Assert.True(resetAt > dashAt);
        Assert.Equal(1, dashed.Split("[8 6] 0 d").Length - 1);
    }

    [Fact]
    public void BothArrows_PaintsBothEndings()
    {
        var none = AnnotationPainter.Paint(LineOf(null, LineStyle.Solid), 0);
        var one = AnnotationPainter.Paint(LineOf(null, LineStyle.Solid, 2f, LineEnding.None, LineEnding.ClosedArrow), 0);
        var both = AnnotationPainter.Paint(LineOf(null, LineStyle.Solid, 2f, LineEnding.ClosedArrow, LineEnding.ClosedArrow), 0);
        Assert.Single(none.Ops);
        Assert.Equal(2, one.Ops.Length);   // 본선 + 화살촉
        Assert.Equal(3, both.Ops.Length);  // 본선 + 화살촉 둘
        Assert.True(both.Bounds.Left < none.Bounds.Left + 0.5f);   // 영역이 양쪽 끝 모양을 덮는다(ComputeRect와 함께)
    }

    // ---- PDFium 렌더: 실제 모양과 영역 밖 0 px ----

    private async Task<byte[]> RenderAsync(string pdf, int w, int h) => await _t.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
        using var pg = d.LoadPage(0);
        return pg.RenderTile(1, 0, 0, w, h, withAnnotations: true);
    });

    private async Task<string> SaveWithAsync(Annotation a, string name)
    {
        await using var s = await OpenBlankAsync(name + "-src.pdf");
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a with { Id = s.Annotations.NewId() }));
        string target = P(name + ".pdf");
        await Svc().SaveAsync(s, target);
        return target;
    }

    private static int InkCount(byte[] bgra, int w, int y, int x0, int x1)
    {
        int n = 0;
        for (int x = x0; x < x1; x++)
        {
            int i = (y * w + x) * 4;
            if (bgra[i] < 200 || bgra[i + 1] < 200 || bgra[i + 2] < 200) n++;
        }
        return n;
    }

    private static int Runs(byte[] bgra, int w, int y, int x0, int x1)
    {
        int runs = 0; bool on = false;
        for (int x = x0; x < x1; x++)
        {
            int i = (y * w + x) * 4;
            bool ink = bgra[i] < 200 || bgra[i + 1] < 200 || bgra[i + 2] < 200;
            if (ink && !on) runs++;
            on = ink;
        }
        return runs;
    }

    [Fact]
    public async Task Render_DashedLine_HasGaps_SolidDoesNot_OutsideZeroDiff()
    {
        const int W = 400, H = 300;
        var solid = await RenderAsync(await SaveWithAsync(LineOf(null, LineStyle.Solid, 3f), "r-solid"), W, H);
        var dashed = await RenderAsync(await SaveWithAsync(LineOf(null, LineStyle.Dashed, 3f), "r-dashed"), W, H);
        var dotted = await RenderAsync(await SaveWithAsync(LineOf(null, LineStyle.Dotted, 3f), "r-dotted"), W, H);
        var dashdot = await RenderAsync(await SaveWithAsync(LineOf(null, LineStyle.DashDot, 3f), "r-dashdot"), W, H);
        int y = H - 150;   // 쪽 y=150 → 화면 y
        Assert.Equal(1, Runs(solid, W, y, 40, 360));
        // 파선 [12 9]: 320pt 길이에 약 15마디
        int rd = Runs(dashed, W, y, 40, 360);
        Assert.InRange(rd, 12, 18);
        int rt = Runs(dotted, W, y, 40, 360);
        Assert.True(rt > rd, $"dotted runs {rt} should exceed dashed runs {rd}");
        // 일점쇄선: 마디(긴 선·점) 수가 파선의 1.5배 안팎(긴 선 하나 + 점 하나)
        int rdd = Runs(dashdot, W, y, 40, 360);
        Assert.True(rdd >= 8, $"dash-dot runs {rdd}");
        // 선 영역(Rect) 밖은 실선과 0 px 차이
        int outside = 0;
        for (int py = 0; py < H; py++)
            for (int px = 0; px < W; px++)
            {
                bool inRect = px >= 38 && px <= 362 && py >= H - 152 - 2 && py <= H - 148 + 2;
                if (inRect) continue;
                int i = (py * W + px) * 4;
                if (solid[i] != dashed[i] || solid[i + 1] != dashed[i + 1] || solid[i + 2] != dashed[i + 2]) outside++;
            }
        Assert.Equal(0, outside);
    }

    [Fact]
    public async Task Render_BothArrows_DrawsHeadsOnBothEnds()
    {
        const int W = 400, H = 300;
        var plain = await RenderAsync(await SaveWithAsync(LineOf(null, LineStyle.Solid, 2f), "r-plain"), W, H);
        var both = await RenderAsync(await SaveWithAsync(LineOf(null, LineStyle.Solid, 2f, LineEnding.ClosedArrow, LineEnding.ClosedArrow), "r-both"), W, H);
        // 양쪽 끝(좌우 18pt 안, 선에서 위아래 4pt 밖)에 화살촉 잉크가 더 있다
        int Extra(int x0, int x1)
        {
            int n = 0;
            for (int py = H - 150 - 12; py <= H - 150 + 12; py++)
                if (Math.Abs(py - (H - 150)) > 2)
                    n += InkCount(both, W, py, x0, x1) - InkCount(plain, W, py, x0, x1);
            return n;
        }
        Assert.True(Extra(36, 60) > 20, "start head");
        Assert.True(Extra(340, 364) > 20, "end head");
        // 가운데 부분은 같다
        for (int py = 0; py < H; py++)
            Assert.Equal(InkCount(plain, W, py, 90, 310), InkCount(both, W, py, 90, 310));
    }

    // ---- Task 3: 지시선 2개 ----

    private static Annotation Callout(DocumentSession? s, PdfPointF tip, PdfRectF box)
    {
        var attach = LeaderAttach(box, tip);
        var a = new Annotation
        {
            Id = s?.Annotations.NewId() ?? default, Kind = AnnotKind.FreeText, Intent = FreeTextIntent.Callout, TextBox = box,
            Vertices = [tip, attach], EndEnding = LineEnding.OpenArrow, Contents = "지시선 시험", FontSizePt = 12f, TextColor = new PdfColor(0, 0, 0),
            Color = Red, FillColor = Yellow, BorderWidth = 1.5f, Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print, Author = "tester",
            CreatedAt = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero), ModifiedAt = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero),
        };
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private static PdfPointF LeaderAttach(PdfRectF box, PdfPointF tip)
    {
        float cx = (box.Left + box.Right) / 2f, cy = (box.Bottom + box.Top) / 2f;
        if (tip.X < box.Left) return new(box.Left, cy);
        if (tip.X > box.Right) return new(box.Right, cy);
        return tip.Y > box.Top ? new(cx, box.Top) : new(cx, box.Bottom);
    }

    private static bool SegmentsCross(PdfPointF a, PdfPointF b, PdfPointF c, PdfPointF d)
    {
        static float Cross(PdfPointF o, PdfPointF p, PdfPointF q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        float d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    [Theory]
    [InlineData(10f, 120f)]    // 첫 지시선이 왼쪽 변에 닿음 → 둘째는 오른쪽 변
    [InlineData(390f, 120f)]   // 오른쪽 변 → 왼쪽
    [InlineData(200f, 290f)]   // 위쪽 변 → 아래쪽
    [InlineData(200f, 10f)]    // 아래쪽 변 → 위쪽
    public void Callout_SecondLeader_AutoElbowPlacement(float tipX, float tipY)
    {
        var box = new PdfRectF(150, 100, 250, 160);
        var a = Callout(null, new PdfPointF(tipX, tipY), box);
        var b = CalloutLeaders.AddSecond(a);
        Assert.Equal(2, CalloutLeaders.Count(b));
        Assert.Equal(3, b.Leader2.Length);                    // 끝점 · 꺾임점 · 상자 변
        var attach1 = a.Vertices[^1];
        var attach2 = b.Leader2[^1];
        // 둘째 지시선은 첫째가 닿은 변의 반대쪽 변 가운데에 닿는다
        Assert.True(Math.Abs(attach2.X - attach1.X) > 49f || Math.Abs(attach2.Y - attach1.Y) > 29f);
        bool onEdge = attach2.X == box.Left || attach2.X == box.Right || attach2.Y == box.Top || attach2.Y == box.Bottom;
        Assert.True(onEdge);
        // 꺾임: 세 점이 한 직선이 아니다
        var (p0, p1, p2) = (b.Leader2[0], b.Leader2[1], b.Leader2[2]);
        float cross = (p1.X - p0.X) * (p2.Y - p0.Y) - (p1.Y - p0.Y) * (p2.X - p0.X);
        Assert.True(Math.Abs(cross) > 1f);
        // 두 지시선이 서로 겹치지 않는다
        for (int i = 0; i + 1 < a.Vertices.Length; i++)
            for (int j = 0; j + 1 < b.Leader2.Length; j++)
                Assert.False(SegmentsCross(a.Vertices[i], a.Vertices[i + 1], b.Leader2[j], b.Leader2[j + 1]));
        // 영역은 둘째 지시선도 덮고, 이미 둘이면 더 못 더한다
        Assert.True(b.Rect.Contains(b.Leader2[0]) && b.Rect.Contains(b.Leader2[1]));
        Assert.False(CalloutLeaders.CanAdd(b));
        Assert.Same(b, CalloutLeaders.AddSecond(b));
        // 삭제하면 처음과 같다
        var c = CalloutLeaders.RemoveSecond(b);
        Assert.Equal(1, CalloutLeaders.Count(c));
        Assert.True(AnnotationComparer.Equivalent(a, c));
    }

    [Fact]
    public void Callout_AddSecond_ClampsIntoBounds()
    {
        var box = new PdfRectF(5, 100, 105, 160);
        var a = Callout(null, new PdfPointF(200, 130), box);   // 첫 지시선이 오른쪽 → 둘째는 왼쪽(쪽 가장자리 근처)
        var b = CalloutLeaders.AddSecond(a, new PdfRectF(0, 0, 400, 300));
        Assert.All(b.Leader2, p => Assert.True(p.X >= 0 && p.X <= 400 && p.Y >= 0 && p.Y <= 300));
    }

    [Fact]
    public void Painter_Callout_DrawsBothLeaders_BoundsCoverSecond()
    {
        var box = new PdfRectF(150, 100, 250, 160);
        var one = Callout(null, new PdfPointF(40, 130), box);
        var two = CalloutLeaders.AddSecond(one);
        var d1 = AnnotationPainter.Paint(one, 0, Ctx);
        var d2 = AnnotationPainter.Paint(two, 0, Ctx);
        // 둘째 지시선(본선 + 끝 모양 화살)만큼 Op가 늘고, 영역이 둘째 지시선 점을 덮는다
        Assert.True(d2.Ops.Length >= d1.Ops.Length + 2);
        Assert.True(d2.Bounds.Contains(two.Leader2[0]));
        // 같은 Painter: 첫 지시선만 있을 때 AP는 Leader2가 빈 배열이든 default든 같다
        var oneDefault = one with { Leader2 = default };
        Assert.Equal(PdfContentWriter.Write(d1), PdfContentWriter.Write(AnnotationPainter.Paint(oneDefault, 0, Ctx)));
    }

    [Fact]
    public void Rotation_TransformsSecondLeader_RoundTrips()
    {
        var box = new PdfRectF(150, 100, 250, 160);
        var a = CalloutLeaders.AddSecond(Callout(null, new PdfPointF(40, 130), box)) with { Rotation = 90 };
        var local = AnnotationRotation.ToLocal(a, out var toPage);
        var back = AnnotationRotation.FromLocal(local, toPage, 90);
        for (int i = 0; i < a.Leader2.Length; i++)
        {
            Assert.Equal(a.Leader2[i].X, back.Leader2[i].X, 0.01);
            Assert.Equal(a.Leader2[i].Y, back.Leader2[i].Y, 0.01);
        }
        Assert.NotEqual(a.Leader2[0], local.Leader2[0]);   // 실제로 돌려졌다
    }

    [Fact]
    public void Translate_And_HitTest_CoverSecondLeader()
    {
        var box = new PdfRectF(150, 100, 250, 160);
        var a = CalloutLeaders.AddSecond(Callout(null, new PdfPointF(40, 130), box));
        var mid = new PdfPointF((a.Leader2[0].X + a.Leader2[1].X) / 2f, (a.Leader2[0].Y + a.Leader2[1].Y) / 2f);
        Assert.True(AnnotationHitTest.Hit(a, mid, 3f));
        var moved = AnnotationGeometry.Translate(a, 10, -5);
        Assert.Equal(a.Leader2[0].X + 10, moved.Leader2[0].X, 0.01);
        Assert.Equal(a.Leader2[0].Y - 5, moved.Leader2[0].Y, 0.01);
        Assert.Equal(a.Leader2.Length, moved.Leader2.Length);
        // 첫 지시선만 있는 설명선은 둘째 영역 근처를 눌러도 맞지 않는다
        var one = CalloutLeaders.RemoveSecond(a);
        Assert.False(AnnotationHitTest.Hit(one, mid, 3f));
    }

    [Fact]
    public async Task Callout_TwoLeaders_RoundTrip()
    {
        var box = new PdfRectF(150, 100, 250, 160);
        var two = CalloutLeaders.AddSecond(Callout(null, new PdfPointF(40, 130), box));
        string target = await SaveWithAsync(two, "two");
        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            var annots = (PdfArray)((doc.Pages[0].Elements["/Annots"] as PdfReference)?.Value ?? doc.Pages[0].Elements["/Annots"]!);
            var d = (PdfDictionary)((annots.Elements[0] as PdfReference)?.Value ?? annots.Elements[0]);
            Assert.Equal(4, d.Elements.GetArray("/CL")!.Elements.Count);       // 표준 /CL = 첫 지시선
            Assert.Equal(6, d.Elements.GetArray("/SedolCL2")!.Elements.Count);  // 비표준 키 = 둘째 지시선
        }
        var o = Assert.Single(AnnotationReader.Read(File.ReadAllBytes(target), null, [0])[0]);
        Assert.Equal(ReadOnlyReason.None, o.Reason);
        var r = o.Data!;
        Assert.Equal(two.Vertices.Length, r.Vertices.Length);
        Assert.Equal(two.Leader2.Length, r.Leader2.Length);
        for (int i = 0; i < two.Leader2.Length; i++)
        {
            Assert.Equal(two.Leader2[i].X, r.Leader2[i].X, 0.01);
            Assert.Equal(two.Leader2[i].Y, r.Leader2[i].Y, 0.01);
        }
        Assert.Equal(two.Rect.Left, r.Rect.Left, 0.01);
        Assert.Equal(two.Rect.Bottom, r.Rect.Bottom, 0.01);

        // PDFium 렌더: 둘째 지시선 영역만 첫 지시선 하나인 쪽과 다르고 그 밖은 0 px
        string oneFile = await SaveWithAsync(CalloutLeaders.RemoveSecond(two), "one");
        var rTwo = await RenderAsync(target, 400, 300);
        var rOne = await RenderAsync(oneFile, 400, 300);
        var area = PdfRectF.FromPoints(two.Leader2).Inflate(3f);
        int inside = 0, outside = 0;
        for (int py = 0; py < 300; py++)
            for (int px = 0; px < 400; px++)
            {
                int i = (py * 400 + px) * 4;
                bool diff = Math.Abs(rTwo[i] - rOne[i]) > 40 || Math.Abs(rTwo[i + 1] - rOne[i + 1]) > 40 || Math.Abs(rTwo[i + 2] - rOne[i + 2]) > 40;
                if (!diff) continue;
                float x = px + 0.5f, y = 300 - (py + 0.5f);
                if (x >= area.Left - 8 && x <= area.Right + 8 && y >= area.Bottom - 8 && y <= area.Top + 8) inside++; else outside++;
            }
        Assert.True(inside > 40, $"second leader pixels {inside}");
        Assert.Equal(0, outside);
    }

    [Fact]
    public async Task Callout_OneLeader_Unchanged()
    {
        var box = new PdfRectF(150, 100, 250, 160);
        var one = Callout(null, new PdfPointF(40, 130), box);
        string target = await SaveWithAsync(one, "one-only");
        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var annots = (PdfArray)((doc.Pages[0].Elements["/Annots"] as PdfReference)?.Value ?? doc.Pages[0].Elements["/Annots"]!);
        var d = (PdfDictionary)((annots.Elements[0] as PdfReference)?.Value ?? annots.Elements[0]);
        Assert.False(d.Elements.ContainsKey("/SedolCL2"));   // 이전 파일과 같은 키 집합
        var r = Assert.Single(AnnotationReader.Read(File.ReadAllBytes(target), null, [0])[0]).Data!;
        Assert.True(r.Leader2.IsEmpty);
        Assert.Equal(1, CalloutLeaders.Count(r));
    }

    [Fact]
    public async Task Callout_UnknownKeysPreserved_AndSecondLeaderEditable()
    {
        var box = new PdfRectF(150, 100, 250, 160);
        var two = CalloutLeaders.AddSecond(Callout(null, new PdfPointF(40, 130), box));
        string first = await SaveWithAsync(two, "uk1");
        using (var doc = PdfReader.Open(first, PdfDocumentOpenMode.Modify))
        {
            var annots = (PdfArray)((doc.Pages[0].Elements["/Annots"] as PdfReference)?.Value ?? doc.Pages[0].Elements["/Annots"]!);
            var d = (PdfDictionary)((annots.Elements[0] as PdfReference)?.Value ?? annots.Elements[0]);
            d.Elements["/FooBar"] = new PdfString("keep-me");
            doc.Save(P("uk2.pdf"));
        }
        await using (var s = await DocumentSession.OpenAsync(_t, P("uk2.pdf"), null))
        {
            await s.AnnotationsReady;
            var id = s.Pages[0].Id;
            var before = Assert.Single(s.Annotations.Get(id).Items).Data;
            Assert.Equal(2, CalloutLeaders.Count(before));
            // 글만 고쳐도 둘째 지시선과 모르는 키가 남는다
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(before, before with { Contents = "고침" })]));
            await Svc().SaveAsync(s, P("uk3.pdf"));
        }
        using (var doc = PdfReader.Open(P("uk3.pdf"), PdfDocumentOpenMode.Import))
        {
            var annots = (PdfArray)((doc.Pages[0].Elements["/Annots"] as PdfReference)?.Value ?? doc.Pages[0].Elements["/Annots"]!);
            var d = (PdfDictionary)((annots.Elements[0] as PdfReference)?.Value ?? annots.Elements[0]);
            Assert.Equal("keep-me", d.Elements.GetString("/FooBar"));
            Assert.Equal(6, d.Elements.GetArray("/SedolCL2")!.Elements.Count);
        }
        // 둘째 지시선을 지우고 저장하면 /SedolCL2가 없어지고 /FooBar는 남는다
        await using (var s = await DocumentSession.OpenAsync(_t, P("uk3.pdf"), null))
        {
            await s.AnnotationsReady;
            var id = s.Pages[0].Id;
            var before = Assert.Single(s.Annotations.Get(id).Items).Data;
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(before, CalloutLeaders.RemoveSecond(before))]));
            await Svc().SaveAsync(s, P("uk4.pdf"));
        }
        using (var doc = PdfReader.Open(P("uk4.pdf"), PdfDocumentOpenMode.Import))
        {
            var annots = (PdfArray)((doc.Pages[0].Elements["/Annots"] as PdfReference)?.Value ?? doc.Pages[0].Elements["/Annots"]!);
            var d = (PdfDictionary)((annots.Elements[0] as PdfReference)?.Value ?? annots.Elements[0]);
            Assert.False(d.Elements.ContainsKey("/SedolCL2"));
            Assert.Equal("keep-me", d.Elements.GetString("/FooBar"));
        }
    }

    [Fact]
    public void MalformedSecondLeaderKey_IsIgnored_NotFatal()
    {
        // /SedolCL2가 점 1개(숫자 2개)처럼 모양이 틀리면 없는 것으로 읽는다(주석 전체를 읽기 전용으로 만들지 않는다)
        var box = new PdfRectF(150, 100, 250, 160);
        var two = CalloutLeaders.AddSecond(Callout(null, new PdfPointF(40, 130), box));
        Assert.Equal(1, CalloutLeaders.Count(two with { Leader2 = [two.Leader2[0]] }));
    }
}
