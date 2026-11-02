using Pdf.Native;
using PdfEditor.Appearance;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests.Annotations;

/// <summary>0.5.0: 긴 점선·이중선·속 빈 화살촉의 저장(패치) → 다시 읽기 왕복과 파일 키(표준 /BS·/LE, 비표준 /SedolLS·/SedolLE).</summary>
public class V050LineStyleRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-v50ls-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly PdfAppearanceSource _ap = new();
    private readonly PdfSharpAnnotationPatcher _patcher = new(() => []);

    public V050LineStyleRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        _patcher.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);
    private SaveService Svc() => new([], _ap, _patcher);

    private async Task<DocumentSession> OpenBlankAsync()
    {
        TestPdfFactory.CreateBlank(P("blank.pdf"), 1, 300, 300);
        var s = await DocumentSession.OpenAsync(_t, P("blank.pdf"), null);
        await s.AnnotationsReady;
        return s;
    }

    private static Annotation Make(DocumentSession s, AnnotKind kind, Func<Annotation, Annotation> f)
    {
        var a = f(new Annotation
        {
            Id = s.Annotations.NewId(), Kind = kind, Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print,
            Author = "tester", Color = new PdfColor(200, 30, 30),
            CreatedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), ModifiedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero),
        });
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private static IReadOnlyList<OriginalAnnotation> ReadBack(string pdf) => AnnotationReader.Read(File.ReadAllBytes(pdf), null, [0])[0];
    private static T Deref<T>(PdfItem? item) where T : PdfItem => (T)((item as PdfReference)?.Value ?? item)!;
    private static PdfDictionary AnnotDict(PdfSharp.Pdf.PdfDocument doc, int index)
        => Deref<PdfDictionary>(Deref<PdfArray>(doc.Pages[0].Elements["/Annots"]).Elements[index]);

    private static ImmutableArrayHelper V(AnnotKind kind) => new(kind);

    private sealed record ImmutableArrayHelper(AnnotKind Kind)
    {
        public System.Collections.Immutable.ImmutableArray<PdfPointF> Pts => Kind == AnnotKind.Line
            ? [new PdfPointF(40, 80), new PdfPointF(240, 120)]
            : [new PdfPointF(40, 80), new PdfPointF(120, 200), new PdfPointF(240, 90)];
    }

    [Theory]
    [InlineData(AnnotKind.Line)]
    [InlineData(AnnotKind.PolyLine)]
    [InlineData(AnnotKind.Polygon)]
    public async Task LongDash_UsesStandardBs(AnnotKind kind)
    {
        await using var s = await OpenBlankAsync();
        var a = Make(s, kind, x => x with
        {
            BorderWidth = 2f, LineStyle = LineStyle.LongDash, Vertices = V(kind).Pts,
            FillColor = kind == AnnotKind.Polygon ? new PdfColor(250, 240, 150) : null,
        });
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        await Svc().SaveAsync(s, P("ld.pdf"));
        var o = Assert.Single(ReadBack(P("ld.pdf")));
        Assert.Equal(ReadOnlyReason.None, o.Reason);
        Assert.Equal(LineStyle.LongDash, o.Data!.LineStyle);
        using var doc = PdfReader.Open(P("ld.pdf"), PdfDocumentOpenMode.Import);
        var d = AnnotDict(doc, 0);
        Assert.Equal("/D", Deref<PdfDictionary>(d.Elements["/BS"]).Elements.GetName("/S"));
        Assert.False(d.Elements.ContainsKey("/SedolLS"));
    }

    [Theory]
    [InlineData(AnnotKind.Line)]
    [InlineData(AnnotKind.PolyLine)]
    [InlineData(AnnotKind.Polygon)]
    public async Task Double_UsesSolidBs_AndSedolKey_AndRoundTrips(AnnotKind kind)
    {
        await using var s = await OpenBlankAsync();
        var a = Make(s, kind, x => x with { BorderWidth = 6f, LineStyle = LineStyle.Double, Vertices = V(kind).Pts });
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        await Svc().SaveAsync(s, P("dbl.pdf"));
        var o = Assert.Single(ReadBack(P("dbl.pdf")));
        Assert.Equal(ReadOnlyReason.None, o.Reason);
        Assert.Equal(LineStyle.Double, o.Data!.LineStyle);
        Assert.Equal(6f, o.Data.BorderWidth);
        using var doc = PdfReader.Open(P("dbl.pdf"), PdfDocumentOpenMode.Import);
        var d = AnnotDict(doc, 0);
        Assert.False(d.Elements.ContainsKey("/BS"));                       // 표준 키로는 실선(다른 프로그램은 AP를 그대로 그린다)
        Assert.Equal("/Double", d.Elements.GetName("/SedolLS"));
        // AP는 가는 선 둘(굵기/3 = 2)
        var ap = Deref<PdfDictionary>(Deref<PdfDictionary>(d.Elements["/AP"]).Elements["/N"]);
        string content = System.Text.Encoding.Latin1.GetString(((PdfDictionary.PdfStream)ap.Stream!).UnfilteredValue);
        Assert.Equal(2, content.Split("2 w\n").Length - 1);
    }

    [Fact]
    public async Task Double_ChangedBackToSolid_RemovesSedolKey()
    {
        await using (var s = await OpenBlankAsync())
        {
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, Make(s, AnnotKind.Line, x => x with
                { BorderWidth = 6f, LineStyle = LineStyle.Double, Vertices = V(AnnotKind.Line).Pts })));
            await Svc().SaveAsync(s, P("a.pdf"));
        }
        await using (var s = await DocumentSession.OpenAsync(_t, P("a.pdf"), null))
        {
            await s.AnnotationsReady;
            var id = s.Pages[0].Id;
            var before = Assert.Single(s.Annotations.Get(id).Items).Data;
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(before, before with { LineStyle = LineStyle.Solid })]));
            await Svc().SaveAsync(s, P("b.pdf"));
        }
        using var doc = PdfReader.Open(P("b.pdf"), PdfDocumentOpenMode.Import);
        Assert.False(AnnotDict(doc, 0).Elements.ContainsKey("/SedolLS"));
        Assert.Equal(LineStyle.Solid, Assert.Single(ReadBack(P("b.pdf"))).Data!.LineStyle);
    }

    [Theory]
    [InlineData(LineEnding.OpenArrow)]
    [InlineData(LineEnding.HollowArrow)]
    [InlineData(LineEnding.ClosedArrow)]
    [InlineData(LineEnding.Diamond)]
    [InlineData(LineEnding.Circle)]
    [InlineData(LineEnding.Square)]
    [InlineData(LineEnding.Slash)]
    [InlineData(LineEnding.Butt)]
    public async Task AllEightHeads_RoundTrip_AndUseStandardLe(LineEnding e)
    {
        await using var s = await OpenBlankAsync();
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, Make(s, AnnotKind.Line, x => x with
            { BorderWidth = 2f, StartEnding = e, EndEnding = LineEnding.None, Vertices = V(AnnotKind.Line).Pts })));
        await Svc().SaveAsync(s, P("e.pdf"));
        var o = Assert.Single(ReadBack(P("e.pdf")));
        Assert.Equal(ReadOnlyReason.None, o.Reason);
        Assert.Equal(e, o.Data!.StartEnding);
        using var doc = PdfReader.Open(P("e.pdf"), PdfDocumentOpenMode.Import);
        var d = AnnotDict(doc, 0);
        var le = Deref<PdfArray>(d.Elements["/LE"]);
        // 표준 /LE에는 항상 ISO 이름만 있다(속 빈 화살촉은 ClosedArrow)
        Assert.Equal(e.ToPdfName(), ((PdfName)Deref<PdfItem>(le.Elements[0])).Value.TrimStart('/'));
        Assert.Equal(e == LineEnding.HollowArrow, d.Elements.ContainsKey("/SedolLE"));
    }
}
