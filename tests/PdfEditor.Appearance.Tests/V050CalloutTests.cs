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
/// 0.5.0 설명선 개선: (A) 지시선 길 [끝점, 꺾임점, 닿는 점]이 화면·AP·/CL에서 같다, (B) 상자 선이 굵어지면 글자도 선 두께만큼 안쪽으로 민다(바깥 /Rect는 그대로).
/// 호환성 샘플 `spikes/out/v050-callout-*.pdf`와 `docs/design/callout-width-*.png`도 여기서 만든다(docs/checklists/viewer-compat.md 0.5.0 절).
/// </summary>
public class V050CalloutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-v050callout-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _font = AppFont.Load(FontTestSupport.FontPath());

    public V050CalloutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        _font.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Root([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static readonly PdfColor Red = new(255, 0, 0);
    private static readonly PdfColor White = new(255, 255, 255);
    private static readonly PdfColor Black = new(0, 0, 0);
    private PaintContext Ctx => new(_font, AnnotationAssets.Empty);

    private static Annotation Make(PdfRectF box, PdfPointF tip, float width, string text = "8989")
    {
        var leader = CalloutLeaders.Route(box, tip);
        var a = new Annotation
        {
            Id = default, Kind = AnnotKind.FreeText, Intent = FreeTextIntent.Callout, TextBox = box, Vertices = leader, EndEnding = LineEnding.OpenArrow,
            Contents = text, FontSizePt = 18f, TextColor = Black, Color = Red, FillColor = White, BorderWidth = width,
            Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print, Author = "0.5.0 샘플",
            CreatedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(9)), ModifiedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(9)),
        };
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    // ---- (A) 지시선 길 ----

    [Fact]
    public void Route_TipLeftOfBox_ElbowGoesStraightOutOfLeftMiddle()
    {
        var box = new PdfRectF(150, 100, 310, 140);
        var r = CalloutLeaders.Route(box, new PdfPointF(60, 20));
        Assert.Equal(3, r.Length);
        Assert.Equal(new PdfPointF(60, 20), r[0]);
        Assert.Equal(new PdfPointF(150, 120), r[2]);                                           // 왼쪽 변 가운데
        Assert.Equal(new PdfPointF(150 - CalloutLeaders.DefaultKneePt, 120), r[1]);            // 수평으로 나간 꺾임점
    }

    [Theory]
    [InlineData(400, 120, 310, 120, 1f)]    // 오른쪽
    [InlineData(230, 20, 230, 100, 0f)]     // 아래(가로 범위 안): 변 가운데 x = 230
    public void Route_OtherSides_AttachAtSideMiddle(float tx, float ty, float ax, float ay, float _)
    {
        var box = new PdfRectF(150, 100, 310, 140);
        var r = CalloutLeaders.Route(box, new PdfPointF(tx, ty));
        Assert.Equal(new PdfPointF(ax, ay), r[^1]);
    }

    [Fact]
    public void Route_TipTooCloseToBend_IsTwoPoints()
    {
        var box = new PdfRectF(150, 100, 310, 140);
        var r = CalloutLeaders.Route(box, new PdfPointF(135, 110));   // 변에서 15pt: 꺾을 자리가 없다
        Assert.Equal(2, r.Length);
        Assert.Equal(new PdfPointF(150, 120), r[1]);
    }

    [Fact]
    public void ReAnchor_TipMovesToAnotherSide_ElbowStandsOutwardOfNewSide_NeverCrossesBox()
    {
        var box = new PdfRectF(100, 100, 520, 180);
        // 아래 변 가운데에서 수직으로 선 꺾임 → 끝점을 왼쪽 아래로 옮기면 왼쪽 변 가운데로 옮겨 바깥(왼쪽)으로 선다
        var leader = CalloutLeaders.Route(box, new PdfPointF(300, 40));
        Assert.Equal(new PdfPointF(310, 100), leader[2]);
        var tipMoved = leader.SetItem(0, new PdfPointF(40, 60));
        var re = CalloutLeaders.ReAnchor(tipMoved, box);
        Assert.Equal(new PdfPointF(100, 140), re[2]);
        Assert.Equal(140f, re[1].Y);
        Assert.True(re[1].X < 100f && re[1].X >= 40f);   // 왼쪽 바깥, 끝점을 지나치지 않음

        // 위·오른쪽도 마찬가지
        var up = CalloutLeaders.ReAnchor(tipMoved.SetItem(0, new PdfPointF(300, 260)), box);
        Assert.Equal(new PdfPointF(310, 180), up[2]);
        Assert.True(up[1].Y > 180f && up[1].X == 310f);
        var right = CalloutLeaders.ReAnchor(tipMoved.SetItem(0, new PdfPointF(600, 90)), box);
        Assert.Equal(new PdfPointF(520, 140), right[2]);
        Assert.True(right[1].X > 520f && right[1].Y == 140f);
    }

    [Fact]
    public void ReAnchor_StandingElbowFollowsBox_DraggedElbowStays()
    {
        var box = new PdfRectF(150, 100, 310, 140);
        var leader = CalloutLeaders.Route(box, new PdfPointF(60, 20));
        var moved = new PdfRectF(150, 100, 310, 180);                      // 위로 키움 → 가운데 y = 140
        var re = CalloutLeaders.ReAnchor(leader, moved);
        Assert.Equal(new PdfPointF(150, 140), re[2]);
        Assert.Equal(new PdfPointF(130, 140), re[1]);

        var dragged = leader.SetItem(1, new PdfPointF(120, 90));           // 사용자가 끌어 둔 꺾임점
        Assert.Equal(new PdfPointF(120, 90), CalloutLeaders.ReAnchor(dragged, moved)[1]);
    }

    [Fact]
    public void Painter_Callout_ThreePointLeader_ArrowTipIsExactlyTheLeaderEnd()
    {
        var box = new PdfRectF(150, 100, 310, 140);
        var tip = new PdfPointF(60, 20);
        foreach (float w in new[] { 1f, 2f, 3f, 6f })
        {
            var d = AnnotationPainter.Paint(Make(box, tip, w), 0, Ctx);
            // 화살촉(열린 화살표) = 꼭짓점이 끝점인 경로. 본선은 끝점에서 시작해 꺾임점·상자로 간다.
            var arrow = d.Ops.Single(o => o.Path.Length == 3 && o.Path[1] is LineTo lt && lt.P == tip);
            Assert.Equal(w, arrow.StrokeWidth);
            var shaft = d.Ops.Single(o => o.Path.Length == 3 && o.Path[0] is MoveTo m && m.P == tip && o.Path[2] is LineTo e && e.P == new PdfPointF(150, 120));
            Assert.Equal(new PdfPointF(150 - CalloutLeaders.DefaultKneePt, 120), ((LineTo)shaft.Path[1]).P);
            Assert.True(d.Bounds.Contains(tip));
        }
    }

    // ---- (B) 선 두께와 글자 안쪽 여백 ----

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(3f)]
    public void Painter_TextInsetGrowsWithBorder_OuterBoxUnchanged(float width)
    {
        var box = new PdfRectF(100, 100, 260, 150);
        var a = Make(box, new PdfPointF(40, 40), width);
        var d = AnnotationPainter.Paint(a, 0, Ctx);
        // 첫 글자 시작 x = 상자 왼쪽 + 기본 여백 + 선 두께
        float x0 = d.Texts.SelectMany(t => t.Glyphs).Min(g => g.Origin.X);
        Assert.Equal(box.Left + FreeTextPainter.Padding + width, x0, 0.01f);
        // 바깥 크기는 그대로: 테두리 경로의 바깥 가장자리가 상자 모서리와 같다
        var frame = d.Ops.First(o => o.Path.Length == 5);
        var r = PdfRectF.FromPoints(frame.Path.OfType<LineTo>().Select(l => l.P).Append(((MoveTo)frame.Path[0]).P).ToArray());
        Assert.Equal(box.Left, r.Left - width / 2f, 0.01f);
        Assert.Equal(box.Right, r.Right + width / 2f, 0.01f);
        Assert.Equal(box, a.TextBox);
    }

    [Fact]
    public void Painter_TextInset_TypewriterAndNoBorder_KeepOldPadding()
    {
        var box = new PdfRectF(100, 100, 260, 150);
        var tw = new Annotation { Kind = AnnotKind.FreeText, Intent = FreeTextIntent.Typewriter, Rect = box, Contents = "x", FontSizePt = 12f, BorderWidth = 3f, Color = Red };
        Assert.Equal(FreeTextPainter.Padding, FreeTextPainter.TextInset(tw));
        var none = tw with { Intent = FreeTextIntent.TextBox, Color = null };
        Assert.Equal(FreeTextPainter.Padding, FreeTextPainter.TextInset(none));
        Assert.Equal(FreeTextPainter.Padding + 3f, FreeTextPainter.TextInset(none with { Color = Red }));
    }

    // ---- PDFium 렌더 + 표본 ----

    private SaveService Service(out PdfSharpAnnotationPatcher patcher)
    {
        patcher = new PdfSharpAnnotationPatcher(_font);
        return new SaveService([], new PdfAppearanceSource(_font), patcher);
    }

    private static bool IsRed(byte[] bgra, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return bgra[i + 2] > 200 && bgra[i + 1] < 80 && bgra[i] < 80;
    }

    private static bool IsDark(byte[] bgra, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return bgra[i] < 90 && bgra[i + 1] < 90 && bgra[i + 2] < 90;
    }

    [Fact]
    public async Task Render_BorderWidths1_2_3_TextStaysInsideBorder_AndWritesPngs()
    {
        const float W = 200, H = 90;
        var docs = Path.Combine(Root(), "docs", "design");
        Directory.CreateDirectory(docs);
        foreach (int bw in new[] { 1, 2, 3 })
        {
            string src = Path.Combine(_dir, $"w{bw}-src.pdf"), target = Path.Combine(_dir, $"w{bw}.pdf");
            TestPdfFactory.CreateBlank(src, 1, W, H);
            var box = new PdfRectF(20, 20, 180, 75);
            using var patcher = new PdfSharpAnnotationPatcher(_font);
            var svc = new SaveService([], new PdfAppearanceSource(_font), patcher);
            await using (var s = await DocumentSession.OpenAsync(_t, src, null))
            {
                await s.AnnotationsReady;
                // 지시선 없는 텍스트 상자 모양(설명선과 같은 상자 그리기)을 위해 일반 텍스트 상자로 만든다.
                var a = Make(box, new PdfPointF(40, 40), bw) with { Intent = FreeTextIntent.TextBox, Vertices = [], TextBox = null, Rect = box };
                await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a with { Id = s.Annotations.NewId() }));
                await svc.SaveAsync(s, target);
            }
            const int Scale = 4;   // 선 두께 차이가 보이게 4배로 그린다
            int pw = (int)W * Scale, ph = (int)H * Scale;
            byte[] px = await _t.InvokeAsync(() =>
            {
                using var d = Pdf.Native.PdfDocument.Open(_t, target);
                using var pg = d.LoadPage(0);
                return pg.RenderTile(Scale, 0, 0, pw, ph, withAnnotations: true);
            });
            PngFile.WriteBgra(Path.Combine(docs, $"callout-width-{bw}.png"), px, pw, ph);

            // 위 칸 y(화면 위 = 페이지 위): 상자 가운데 가로줄에서 왼쪽 선 두께와 글자 시작까지의 간격을 잰다.
            int row = (int)((H - 47) * Scale);
            int left = (int)(box.Left * Scale);
            int redRun = 0;
            while (IsRed(px, pw, left + redRun, row)) redRun++;
            Assert.InRange(redRun, bw * Scale - 2, bw * Scale + 2);                  // 선 두께만큼
            int firstDark = -1;
            for (int y = (int)((H - 75) * Scale) + 1; y < (int)((H - 20) * Scale) && firstDark < 0; y++)
                for (int x = left + redRun; x < (int)(box.Right * Scale) - 8; x++)
                    if (IsDark(px, pw, x, y)) { firstDark = x; break; }
            Assert.True(firstDark >= left + (FreeTextPainter.Padding + bw) * Scale - 2, $"w{bw}: 글자가 선/안쪽 여백 안으로 들어왔다 ({firstDark - left})");
        }
    }

    [Fact]
    public async Task CreateCalloutSample()
    {
        const float W = 595, H = 842;
        var outDir = Path.Combine(Root(), "spikes", "out");
        Directory.CreateDirectory(outDir);
        var target = Path.Combine(outDir, "v050-callout-shapes.pdf");
        try { if (File.Exists(target)) using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
        catch (IOException) { target = Path.Combine(outDir, "v050-callout-shapes-new.pdf"); }   // 뷰어가 열어 둔 샘플은 덮어쓸 수 없다
        string blank = Path.Combine(_dir, "blank.pdf");
        TestPdfFactory.CreateBlank(blank, 2, W, H);
        var svc = Service(out var patcher);
        using var _ = patcher;

        await using (var s = await DocumentSession.OpenAsync(_t, blank, null))
        {
            await s.AnnotationsReady;
            var p1 = s.Pages[0].Id; var p2 = s.Pages[1].Id;
            await s.Commands.ExecuteAsync(new RotatePagesCommand([p2], 1));
            async Task Add(PageId p, Annotation a) => await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(p, a with { Id = s.Annotations.NewId() }));

            // 선 두께 1·2·3: 안쪽으로 늘고 글자가 밀린다
            float x = 40;
            foreach (float bw in new[] { 1f, 2f, 3f })
            {
                await Add(p1, Make(new PdfRectF(x, 740, x + 160, 800), new PdfPointF(x + 20, 690), bw) with { Contents = "8989" });
                x += 180;
            }
            // 왼쪽·오른쪽·아래·위, 가까운 끝점(꺾임 없음)
            await Add(p1, Make(new PdfRectF(300, 600, 460, 650), new PdfPointF(100, 520), 1.5f) with { Contents = "왼쪽 아래로" });
            await Add(p1, Make(new PdfRectF(60, 440, 220, 490), new PdfPointF(400, 380), 1.5f) with { Contents = "오른쪽 아래로" });
            await Add(p1, Make(new PdfRectF(300, 300, 460, 350), new PdfPointF(380, 220), 3f) with { Contents = "아래로(가로 범위 안)" });
            await Add(p1, Make(new PdfRectF(300, 100, 460, 150), new PdfPointF(380, 190), 1.5f) with { Contents = "위로" });
            await Add(p1, Make(new PdfRectF(60, 100, 220, 150), new PdfPointF(40, 130), 1.5f) with { Contents = "가까운 끝점" });
            // 둘째 지시선도 같은 길 모양을 거친다
            var two = Make(new PdfRectF(60, 300, 220, 350), new PdfPointF(30, 240), 1.5f) with { Contents = "지시선 2개" };
            await Add(p1, CalloutLeaders.AddSecond(two));
            // 회전한 쪽
            await Add(p2, Make(new PdfRectF(200, 400, 400, 460), new PdfPointF(60, 330), 3f) with { Contents = "회전한 쪽", Rotation = 90 });
            await svc.SaveAsync(s, target);
        }

        var back = AnnotationReader.Read(File.ReadAllBytes(target), null, [0, 1]);
        Assert.All(back[0], o => Assert.Equal(ReadOnlyReason.None, o.Reason));
        Assert.Equal(9, back[0].Count);
        Assert.Single(back[1]);
        // 저장한 /CL이 Route 모양(3점: 끝점·꺾임점·상자 변 가운데)으로 다시 읽힌다
        var first = back[0].Select(o => o.Data!).First(d => d.Contents == "8989");
        Assert.Equal(3, first.Vertices.Length);
        Assert.Equal(first.TextBox!.Value.Bottom, first.Vertices[2].Y, 0.01f);          // 끝점이 아래(가로 범위 안) → 아래 변 가운데
        Assert.Equal(first.Vertices[2].X, first.Vertices[1].X, 0.01f);                   // 꺾임점은 변에서 수직으로 선다
        Assert.True(first.Vertices[1].Y < first.Vertices[2].Y);
        // 같은 쪽을 PDFium으로 렌더해 본다(저장된 AP 그대로)
        await _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(_t, target);
            using var page = doc.LoadPage(0);
            var size = new PageTransform(page.BoundsPt, page.Rotation, 1.0).ViewSize;
            int pw = (int)Math.Ceiling(size.W), ph = (int)Math.Ceiling(size.H);
            var px = page.RenderTile(1.0, 0, 0, pw, ph, withAnnotations: true);
            PngFile.WriteBgra(Path.Combine(outDir, "v050-callout-shapes-p1.png"), px, pw, ph);
            Assert.Contains(Enumerable.Range(0, pw * ph), i => IsRed(px, pw, i % pw, i / pw));
            return 0;
        });
    }
}
