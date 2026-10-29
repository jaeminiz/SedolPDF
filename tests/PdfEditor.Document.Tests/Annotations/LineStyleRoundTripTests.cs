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

/// <summary>0.4.0 Task 2: 선 모양(실선·점선·파선·일점쇄선)과 양쪽 끝 모양의 모델 → 저장(패치 단계) → 다시 읽기 왕복, /BS 키 규칙.</summary>
public class LineStyleRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-lsrt-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly PdfAppearanceSource _ap = new();
    private readonly PdfSharpAnnotationPatcher _patcher = new(() => []);

    public LineStyleRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        _patcher.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);
    private SaveService Svc() => new([], _ap, _patcher);

    private async Task<DocumentSession> OpenBlankAsync(string name = "blank.pdf")
    {
        TestPdfFactory.CreateBlank(P(name), 1, 300, 300);
        var s = await DocumentSession.OpenAsync(_t, P(name), null);
        await s.AnnotationsReady;
        return s;
    }

    private static Annotation Make(DocumentSession s, AnnotKind kind, Func<Annotation, Annotation> f)
    {
        var a = f(new Annotation
        {
            Id = s.Annotations.NewId(), Kind = kind, Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print,
            Author = "tester", Color = new PdfColor(200, 30, 30),
            CreatedAt = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero), ModifiedAt = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero),
        });
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private static Annotation Sample(DocumentSession s, AnnotKind kind, LineStyle style, float width) => kind switch
    {
        AnnotKind.Line => Make(s, kind, a => a with
        {
            BorderWidth = width, LineStyle = style, StartEnding = LineEnding.ClosedArrow, EndEnding = LineEnding.OpenArrow,
            Vertices = [new PdfPointF(40, 80), new PdfPointF(240, 120)],
        }),
        AnnotKind.PolyLine => Make(s, kind, a => a with
        {
            BorderWidth = width, LineStyle = style, StartEnding = LineEnding.Circle, EndEnding = LineEnding.Diamond,
            Vertices = [new PdfPointF(40, 150), new PdfPointF(120, 220), new PdfPointF(200, 160), new PdfPointF(260, 230)],
        }),
        _ => Make(s, kind, a => a with
        {
            BorderWidth = width, LineStyle = style, FillColor = new PdfColor(255, 240, 150),
            Vertices = [new PdfPointF(60, 20), new PdfPointF(240, 30), new PdfPointF(180, 70)],
        }),
    };

    private static IReadOnlyList<OriginalAnnotation> ReadBack(string pdf) => AnnotationReader.Read(File.ReadAllBytes(pdf), null, [0])[0];

    private static T Deref<T>(PdfItem? item) where T : PdfItem => (T)((item as PdfReference)?.Value ?? item)!;

    private static PdfDictionary AnnotDict(PdfSharp.Pdf.PdfDocument doc, int index)
        => Deref<PdfDictionary>(Deref<PdfArray>(doc.Pages[0].Elements["/Annots"]).Elements[index]);

    private static double[] Nums(PdfArray a) => a.Elements.Select(i => i switch { PdfInteger n => (double)n.Value, PdfReal r => r.Value, _ => double.NaN }).ToArray();

    [Fact]
    public void LineStyle_AppliesTable_IsFixed()
    {
        // 적용 대상 표를 코드에 고정한다: 선·폴리라인·다각형만(사각형·타원·연필·글 상자는 이번 판에 넣지 않음, 0.5.0 후보).
        var applies = Enum.GetValues<AnnotKind>().Where(k => k.Applies()).ToArray();
        Assert.Equal([AnnotKind.Line, AnnotKind.PolyLine, AnnotKind.Polygon], applies);
        Assert.All(applies, k => Assert.True(k.IsPatchKind()));   // 모두 패치 길(저장 때 /BS를 쓸 수 있다)
    }

    [Theory]
    [InlineData(AnnotKind.Line, LineStyle.Dotted, 2f)]
    [InlineData(AnnotKind.Line, LineStyle.Dashed, 1f)]
    [InlineData(AnnotKind.Line, LineStyle.DashDot, 3.5f)]
    [InlineData(AnnotKind.PolyLine, LineStyle.Dashed, 2.5f)]
    [InlineData(AnnotKind.PolyLine, LineStyle.DashDot, 1.5f)]
    [InlineData(AnnotKind.Polygon, LineStyle.Dotted, 1f)]
    [InlineData(AnnotKind.Polygon, LineStyle.Dashed, 4f)]
    public async Task Line_RoundTrip_PreservesStyleWidthEndings(AnnotKind kind, LineStyle style, float width)
    {
        await using var s = await OpenBlankAsync();
        var a = Sample(s, kind, style, width);
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        string target = P("ls.pdf");
        await Svc().SaveAsync(s, target);

        var o = Assert.Single(ReadBack(target));
        Assert.Equal(ReadOnlyReason.None, o.Reason);   // 점선·파선·일점쇄선도 편집할 수 있게 읽힌다(이전에는 읽기 전용이었다)
        Assert.Equal(style, o.Data!.LineStyle);
        Assert.Equal(width, o.Data.BorderWidth);
        Assert.Equal(a.StartEnding, o.Data.StartEnding);
        Assert.Equal(a.EndEnding, o.Data.EndEnding);

        // 파일의 표준 /BS: /S /D, /D = 굵기 비례 배열, /W = 굵기
        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var d = AnnotDict(doc, 0);
        var bs = Deref<PdfDictionary>(d.Elements["/BS"]);
        Assert.Equal("/D", bs.Elements.GetName("/S"));
        Assert.Equal(width, Nums(new PdfArray(doc) { Elements = { bs.Elements["/W"]! } })[0], 3);
        var expected = LineStyles.DashArray(style, width);
        var actual = Nums(Deref<PdfArray>(bs.Elements["/D"]));
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i], 2);
    }

    [Theory]
    [InlineData(AnnotKind.Line)]
    [InlineData(AnnotKind.PolyLine)]
    [InlineData(AnnotKind.Polygon)]
    public async Task Solid_WritesNoBs_AsBefore(AnnotKind kind)
    {
        await using var s = await OpenBlankAsync();
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, Sample(s, kind, LineStyle.Solid, 2f)));
        string target = P("solid.pdf");
        await Svc().SaveAsync(s, target);
        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var d = AnnotDict(doc, 0);
        Assert.False(d.Elements.ContainsKey("/BS"));          // 이전 파일과 같다(/Border만)
        Assert.Equal(LineStyle.Solid, Assert.Single(ReadBack(target)).Data!.LineStyle);
        // AP 내용 스트림에 대시 연산자가 없다
        var ap = Deref<PdfDictionary>(Deref<PdfDictionary>(d.Elements["/AP"]).Elements["/N"]);
        string content = System.Text.Encoding.Latin1.GetString(((PdfDictionary.PdfStream)ap.Stream!).UnfilteredValue);
        Assert.DoesNotContain(" d\n", content);
    }

    [Fact]
    public async Task EditedOriginal_KeepsUnknownBsKeys_AndRestyles()
    {
        // 1) 점선 선 하나를 저장한다.
        await using (var s = await OpenBlankAsync())
        {
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, Sample(s, AnnotKind.Line, LineStyle.Dashed, 2f)));
            await Svc().SaveAsync(s, P("orig.pdf"));
        }
        // 2) 다른 프로그램이 /BS에 모르는 키를 더한 것처럼 만든다.
        using (var doc = PdfReader.Open(P("orig.pdf"), PdfDocumentOpenMode.Modify))
        {
            var bs = Deref<PdfDictionary>(AnnotDict(doc, 0).Elements["/BS"]);
            bs.Elements["/FooBar"] = new PdfString("keep-me");
            doc.Save(P("orig2.pdf"));
        }
        // 3) 연 뒤 색만 바꿔 저장 → /FooBar 보존 + 점선 유지
        await using (var s = await DocumentSession.OpenAsync(_t, P("orig2.pdf"), null))
        {
            await s.AnnotationsReady;
            var id = s.Pages[0].Id;
            var before = Assert.Single(s.Annotations.Get(id).Items).Data;
            Assert.Equal(LineStyle.Dashed, before.LineStyle);
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(before, before with { Color = new PdfColor(0, 0, 255) })]));
            await Svc().SaveAsync(s, P("edited.pdf"));
        }
        using (var doc = PdfReader.Open(P("edited.pdf"), PdfDocumentOpenMode.Import))
        {
            var bs = Deref<PdfDictionary>(AnnotDict(doc, 0).Elements["/BS"]);
            Assert.Equal("keep-me", bs.Elements.GetString("/FooBar"));
            Assert.Equal("/D", bs.Elements.GetName("/S"));
        }
        Assert.Equal(LineStyle.Dashed, Assert.Single(ReadBack(P("edited.pdf"))).Data!.LineStyle);

        // 4) 실선으로 되돌리면 /BS가 없어진다
        await using (var s = await DocumentSession.OpenAsync(_t, P("edited.pdf"), null))
        {
            await s.AnnotationsReady;
            var id = s.Pages[0].Id;
            var before = Assert.Single(s.Annotations.Get(id).Items).Data;
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(before, before with { LineStyle = LineStyle.Solid })]));
            await Svc().SaveAsync(s, P("solid2.pdf"));
        }
        using (var doc = PdfReader.Open(P("solid2.pdf"), PdfDocumentOpenMode.Import))
            Assert.False(AnnotDict(doc, 0).Elements.ContainsKey("/BS"));
    }

    [Fact]
    public async Task ForeignDashPattern_StaysReadOnly()
    {
        // 굵기 비례가 아닌 다른 프로그램의 점선 배열은 인식하지 못한다 → 이전처럼 읽기 전용(StyledBorder)
        await using (var s = await OpenBlankAsync())
        {
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, Sample(s, AnnotKind.Line, LineStyle.Dashed, 2f)));
            await Svc().SaveAsync(s, P("f1.pdf"));
        }
        using (var doc = PdfReader.Open(P("f1.pdf"), PdfDocumentOpenMode.Modify))
        {
            var bs = Deref<PdfDictionary>(AnnotDict(doc, 0).Elements["/BS"]);
            var arr = new PdfArray(doc);
            arr.Elements.Add(new PdfReal(5.7));
            arr.Elements.Add(new PdfReal(1.3));
            bs.Elements["/D"] = arr;
            doc.Save(P("f2.pdf"));
        }
        var o = Assert.Single(ReadBack(P("f2.pdf")));
        Assert.Equal(ReadOnlyReason.StyledBorder, o.Reason);
    }

    [Fact]
    public void BothArrows_SetsStartAndEnd_ViaModel()
    {
        // 모델 수준: 시작·끝 모양은 /LE [a b] 그대로 저장된다 — 양쪽 화살표는 둘을 같은 값으로 두는 것이다(UI 단추는 App 시험).
        var a = new Annotation { Kind = AnnotKind.Line, StartEnding = LineEnding.OpenArrow, EndEnding = LineEnding.OpenArrow };
        Assert.Equal("OpenArrow", a.StartEnding.ToPdfName());
        Assert.Equal(a.StartEnding, a.EndEnding);
    }
}
