using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Commands;
using PdfEditor.Exchange.Xfdf;
using PdfEditor.TestFixtures;

namespace PdfEditor.Exchange.Tests;

/// <summary>0.5.0: 선 종류(긴 점선·이중선)·속 빈 화살촉·설명선 지시선 모양과 글자 서식의 XFDF 내보내기 → 가져오기.</summary>
public class V050StyleXfdfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-v50x-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly List<DocumentSession> _sessions = [];
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(9));

    public V050StyleXfdfTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var s in _sessions) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<DocumentSession> OpenBlankAsync()
    {
        string p = Path.Combine(_dir, "blank.pdf");
        TestPdfFactory.CreateBlank(p, 1, 300, 300);
        var s = await DocumentSession.OpenAsync(_t, p, null);
        _sessions.Add(s);
        await s.AnnotationsReady;
        return s;
    }

    private static Annotation Make(DocumentSession s, AnnotKind k, string nm, Func<Annotation, Annotation> f)
    {
        var a = f(new Annotation
        {
            Id = s.Annotations.NewId(), Kind = k, Rect = new PdfRectF(10, 20, 200, 120), Color = new PdfColor(255, 0, 0), Name = nm, Author = "Kim",
            CreatedAt = T0, ModifiedAt = T0, Flags = PdfAnnotFlags.Print,
        });
        return a;
    }

    private static IReadOnlyList<Annotation> RoundTrip(DocumentSession s, IEnumerable<Annotation> src, out string xml)
    {
        var doc = XfdfWriter.Write([.. src.Select(a => (0, a))], "x.pdf", new AnnotationAssets());
        xml = doc.ToString();
        using var ms = new MemoryStream();
        doc.Save(ms);
        ms.Position = 0;
        var r = XfdfReader.Read(ms, s.Annotations.NewId, new AnnotationAssets());
        Assert.Empty(r.Skipped);
        return [.. r.Annotations.Select(i => i.A)];
    }

    [Theory]
    [InlineData(AnnotKind.Line, LineStyle.LongDash)]
    [InlineData(AnnotKind.PolyLine, LineStyle.Double)]
    [InlineData(AnnotKind.Polygon, LineStyle.Double)]
    [InlineData(AnnotKind.Polygon, LineStyle.LongDash)]
    [InlineData(AnnotKind.Line, LineStyle.DashDot)]
    public async Task LineStyle_RoundTrips(AnnotKind kind, LineStyle style)
    {
        var s = await OpenBlankAsync();
        var a = Make(s, kind, "ls", x => x with
        {
            BorderWidth = 3f, LineStyle = style,
            Vertices = kind == AnnotKind.Line ? [new PdfPointF(10, 10), new PdfPointF(100, 90)] : [new PdfPointF(10, 10), new PdfPointF(100, 90), new PdfPointF(60, 150)],
        });
        var back = Assert.Single(RoundTrip(s, [a], out string xml));
        Assert.Equal(style, back.LineStyle);
        if (style == LineStyle.Double) Assert.Contains("sedolls=\"Double\"", xml);   // 비표준 속성
        else Assert.Contains("style=\"dash\"", xml);                                  // 표준 속성
    }

    [Theory]
    [InlineData(LineEnding.OpenArrow, LineEnding.HollowArrow)]
    [InlineData(LineEnding.HollowArrow, LineEnding.ClosedArrow)]
    [InlineData(LineEnding.Butt, LineEnding.Slash)]
    [InlineData(LineEnding.Diamond, LineEnding.Circle)]
    [InlineData(LineEnding.Square, LineEnding.None)]
    public async Task Endings_RoundTrip_HollowKeepsStandardName(LineEnding start, LineEnding end)
    {
        var s = await OpenBlankAsync();
        var a = Make(s, AnnotKind.Line, "le", x => x with { BorderWidth = 2f, StartEnding = start, EndEnding = end, Vertices = [new PdfPointF(10, 10), new PdfPointF(100, 90)] });
        var back = Assert.Single(RoundTrip(s, [a], out string xml));
        Assert.Equal(start, back.StartEnding);
        Assert.Equal(end, back.EndEnding);
        Assert.DoesNotContain(" head=\"HollowArrow\"", xml);
        Assert.DoesNotContain(" tail=\"HollowArrow\"", xml);
    }

    [Fact]
    public async Task Callout_LeaderStyle_AndHollowHead_RoundTrip()
    {
        var s = await OpenBlankAsync();
        var a = Make(s, AnnotKind.FreeText, "co", x => x with
        {
            Intent = FreeTextIntent.Callout, Contents = "x", FontSizePt = 12, TextColor = new PdfColor(0, 0, 0), BorderWidth = 2f,
            Vertices = [new PdfPointF(20, 30), new PdfPointF(80, 60)], TextBox = new PdfRectF(80, 50, 180, 100),
            LineStyle = LineStyle.LongDash, EndEnding = LineEnding.HollowArrow,
        });
        var back = Assert.Single(RoundTrip(s, [a], out _));
        Assert.Equal(LineStyle.LongDash, back.LineStyle);
        Assert.Equal(LineEnding.HollowArrow, back.EndEnding);
    }
}
