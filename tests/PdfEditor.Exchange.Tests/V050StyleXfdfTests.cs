using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Commands;
using PdfEditor.Exchange.Xfdf;
using PdfEditor.TestFixtures;

namespace PdfEditor.Exchange.Tests;

/// <summary>0.5.0: 글자 서식의 XFDF 내보내기 → 가져오기.</summary>
[Collection("XfdfHardening")]   // PDFium을 쓰는 다른 XFDF 시험과 동시에 돌 때 시험 호스트가 죽는 일이 있어 혼자 실행
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
    [InlineData(AnnotKind.FreeText, FreeTextIntent.TextBox, true, false, true, false, TextFontFamily.Serif)]
    [InlineData(AnnotKind.FreeText, FreeTextIntent.Typewriter, false, true, false, true, TextFontFamily.Sans)]
    [InlineData(AnnotKind.FreeText, FreeTextIntent.Callout, true, true, true, true, TextFontFamily.Serif)]
    public async Task TextFormat_RoundTrips(AnnotKind kind, FreeTextIntent intent, bool bold, bool italic, bool underline, bool strike, TextFontFamily family)
    {
        var s = await OpenBlankAsync();
        var a = Make(s, kind, "tf", x => x with
        {
            Intent = intent, Contents = "hi", FontSizePt = 14, TextColor = new PdfColor(0, 0, 0), BorderWidth = 1f,
            Vertices = intent == FreeTextIntent.Callout ? [new PdfPointF(20, 30), new PdfPointF(80, 60)] : [],
            TextBox = intent == FreeTextIntent.Callout ? new PdfRectF(80, 50, 180, 100) : null,
            Bold = bold, Italic = italic, Underline = underline, StrikeThrough = strike, TextFamily = family,
        });
        var back = Assert.Single(RoundTrip(s, [a], out string xml));
        Assert.Equal((bold, italic, underline, strike, family), (back.Bold, back.Italic, back.Underline, back.StrikeThrough, back.TextFamily));
        Assert.Equal(family == TextFontFamily.Serif, xml.Contains("sedolfont=\"Serif\""));
    }

    [Fact]
    public async Task TextFormat_DefaultWritesNoAttributes_UnknownValuesIgnored()
    {
        var s = await OpenBlankAsync();
        var a = Make(s, AnnotKind.FreeText, "tf0", x => x with { Contents = "hi", FontSizePt = 12, TextColor = new PdfColor(0, 0, 0) });
        RoundTrip(s, [a], out string xml);
        Assert.DoesNotContain("sedolfont", xml);
        Assert.DoesNotContain("sedolfmt", xml);
        var edited = xml.Replace("<freetext ", "<freetext sedolfont=\"Comic\" sedolfmt=\"255\" ");
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(edited));
        var r = XfdfReader.Read(ms, s.Annotations.NewId, new AnnotationAssets());
        var back = Assert.Single(r.Annotations).A;
        Assert.Equal(TextFontFamily.Sans, back.TextFamily);
        Assert.True(back.Bold && back.Italic && back.Underline && back.StrikeThrough);   // 알 수 없는 비트만 버린다
    }
}
