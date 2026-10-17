using Pdf.Native;
using PdfEditor.Appearance;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests.Annotations;

/// <summary>선·폴리라인·다각형·클라우드·Caret: 모델 → 저장(패치 단계) → 다시 읽기 / PDFium으로 다시 열기 (계획 Task 9).</summary>
public class PatchKindsRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-pkrt-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly PdfAppearanceSource _ap = new();
    private readonly PdfSharpAnnotationPatcher _patcher = new(() => []);

    private static readonly PdfColor Red = new(229, 55, 45);
    private static readonly PdfColor Yellow = new(255, 235, 0);
    private static readonly PdfColor Blue = new(30, 90, 220);

    public PatchKindsRoundTripTests() => Directory.CreateDirectory(_dir);

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
        TestPdfFactory.CreateBlank(P(name), 1, 200, 200);
        var s = await DocumentSession.OpenAsync(_t, P(name), null);
        await s.AnnotationsReady;
        return s;
    }

    private static Annotation Make(DocumentSession s, AnnotKind kind, PdfRectF rect, Func<Annotation, Annotation> f)
        => f(new Annotation
        {
            Id = s.Annotations.NewId(), Kind = kind, Rect = rect, Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print,
            Author = "tester", Subject = "subj",
            CreatedAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero), ModifiedAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero),
        });

    private static Annotation Arrow(DocumentSession s)
    {
        var a = Make(s, AnnotKind.Line, default, x => x with
        {
            Color = Red, FillColor = Yellow, BorderWidth = 3, Opacity = 0.8f, Contents = "선 메모",
            Vertices = [new PdfPointF(40, 80), new PdfPointF(160, 120)],
            StartEnding = LineEnding.Circle, EndEnding = LineEnding.ClosedArrow,
        });
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private static IReadOnlyList<OriginalAnnotation> ReadBack(string pdf)
        => AnnotationReader.Read(File.ReadAllBytes(pdf), null, [0])[0];

    private static T Deref<T>(PdfItem? item) where T : PdfItem => (T)((item as PdfReference)?.Value ?? item)!;

    private static PdfDictionary AnnotDict(PdfSharp.Pdf.PdfDocument doc, int index)
        => Deref<PdfDictionary>(Deref<PdfArray>(doc.Pages[0].Elements["/Annots"]).Elements[index]);

    private static int AnnotCount(PdfSharp.Pdf.PdfDocument doc)
        => doc.Pages[0].Elements["/Annots"] is { } a ? Deref<PdfArray>(a).Elements.Count : 0;

    private static double[] Nums(PdfDictionary d, string key)
        => Deref<PdfArray>(d.Elements[key]).Elements.Select(i => i switch { PdfInteger n => (double)n.Value, PdfReal r => r.Value, _ => double.NaN }).ToArray();

    private static void AssertRect(PdfRectF expected, PdfRectF actual)
    {
        Assert.Equal(expected.Left, actual.Left, 0.01);
        Assert.Equal(expected.Bottom, actual.Bottom, 0.01);
        Assert.Equal(expected.Right, actual.Right, 0.01);
        Assert.Equal(expected.Top, actual.Top, 0.01);
    }

    private static void AssertPoints(IReadOnlyList<PdfPointF> expected, IReadOnlyList<PdfPointF> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].X, actual[i].X, 0.01);
            Assert.Equal(expected[i].Y, actual[i].Y, 0.01);
        }
    }

    /// <summary>저장 파일을 PDFium으로 다시 열어 쪽 0의 주석 (하위 유형, 번호) 목록을 본다.</summary>
    private Task<List<int>> PdfiumSubtypes(string pdf) => _t.InvokeAsync(() =>
    {
        using var doc = Pdf.Native.PdfDocument.Open(_t, pdf);
        using var page = doc.LoadPage(0);
        var list = new List<int>();
        for (int i = 0; i < page.AnnotationCount; i++)
        {
            using var a = page.GetAnnotation(i);
            list.Add(a.Subtype);
        }
        return list;
    });

    [Fact]
    public async Task Line_Arrow_SaveReopen_Equivalent()
    {
        await using var s = await OpenBlankAsync();
        var a = Arrow(s);
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        string target = P("line.pdf");
        await Svc().SaveAsync(s, target);

        var o = Assert.Single(ReadBack(target));
        Assert.Equal(ReadOnlyReason.None, o.Reason);
        var r = o.Data!;
        Assert.Equal(AnnotKind.Line, r.Kind);
        AssertPoints(a.Vertices, r.Vertices);
        Assert.Equal(LineEnding.Circle, r.StartEnding);
        Assert.Equal(LineEnding.ClosedArrow, r.EndEnding);
        Assert.Equal(Red, r.Color);
        Assert.Equal(Yellow, r.FillColor);
        Assert.Equal(3f, r.BorderWidth);
        Assert.Equal(0.8f, r.Opacity, 3);
        Assert.Equal("선 메모", r.Contents);
        Assert.Equal("tester", r.Author);
        AssertRect(a.Rect, r.Rect);

        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(1, AnnotCount(doc));
            var d = AnnotDict(doc, 0);
            Assert.Equal("/Line", d.Elements.GetName("/Subtype"));
            Assert.Equal(4, d.Elements.GetInteger("/F") & 4);
            Assert.Equal([40d, 80d, 160d, 120d], Nums(d, "/L"));
            // /LE는 이름 둘의 배열
            var le = Deref<PdfArray>(d.Elements["/LE"]);
            Assert.Equal(["/Circle", "/ClosedArrow"], le.Elements.Select(i => ((PdfName)i).Value).ToArray());
            // 투명도는 /CA와 AP의 /ExtGState /GS 둘 다
            Assert.Equal(0.8, ((PdfReal)d.Elements["/CA"]!).Value, 3);
            var form = Deref<PdfDictionary>(Deref<PdfDictionary>(d.Elements["/AP"]).Elements["/N"]);
            var gs = Deref<PdfDictionary>(Deref<PdfDictionary>(Deref<PdfDictionary>(form.Elements["/Resources"]).Elements["/ExtGState"]).Elements["/GS"]);
            Assert.Equal(0.8, ((PdfReal)gs.Elements["/CA"]!).Value, 3);
        }

        // PDFium으로 다시 열면 Line 하나(= Subtype Line)
        Assert.Equal([PdfAnnotSubtype.Line], await PdfiumSubtypes(target));
    }

    [Fact]
    public async Task PolyLine_Polygon_SaveReopen_Equivalent()
    {
        await using var s = await OpenBlankAsync();
        var pl = Make(s, AnnotKind.PolyLine, default, x => x with
        {
            Color = Blue, BorderWidth = 2.5f, StartEnding = LineEnding.Diamond, EndEnding = LineEnding.OpenArrow,
            Vertices = [new PdfPointF(35, 60), new PdfPointF(80, 140), new PdfPointF(120, 70), new PdfPointF(165, 135)],
        });
        pl = pl with { Rect = AnnotationGeometry.ComputeRect(pl) };
        var pg = Make(s, AnnotKind.Polygon, default, x => x with
        {
            Color = Red, FillColor = Yellow, BorderWidth = 2,
            Vertices = [new PdfPointF(40, 20), new PdfPointF(160, 30), new PdfPointF(120, 50)],
        });
        pg = pg with { Rect = AnnotationGeometry.ComputeRect(pg) };
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, pl));
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, pg));
        string target = P("poly.pdf");
        await Svc().SaveAsync(s, target);

        var read = ReadBack(target);
        Assert.Equal(2, read.Count);
        Assert.All(read, o => Assert.Equal(ReadOnlyReason.None, o.Reason));
        var rl = read.Single(o => o.Data!.Kind == AnnotKind.PolyLine).Data!;
        AssertPoints(pl.Vertices, rl.Vertices);
        Assert.Equal(LineEnding.Diamond, rl.StartEnding);
        Assert.Equal(LineEnding.OpenArrow, rl.EndEnding);
        Assert.Equal(2.5f, rl.BorderWidth);
        Assert.Equal(Blue, rl.Color);
        AssertRect(pl.Rect, rl.Rect);
        var rp = read.Single(o => o.Data!.Kind == AnnotKind.Polygon).Data!;
        AssertPoints(pg.Vertices, rp.Vertices);
        Assert.Equal(Yellow, rp.FillColor);
        Assert.Equal(Red, rp.Color);
        Assert.Equal(0f, rp.CloudIntensity);

        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            var pld = AnnotDict(doc, 0);
            Assert.Equal("/PolyLine", pld.Elements.GetName("/Subtype"));
            Assert.Equal(pl.Vertices.SelectMany(v => new[] { (double)v.X, v.Y }), Nums(pld, "/Vertices"));
            Assert.False(AnnotDict(doc, 1).Elements.ContainsKey("/BE"));
        }
        Assert.Equal([PdfAnnotSubtype.PolyLine, PdfAnnotSubtype.Polygon], await PdfiumSubtypes(target));
    }

    [Fact]
    public async Task CloudSquare_PdfiumPathPlusBePatch()
    {
        await using var s = await OpenBlankAsync();
        var a = Make(s, AnnotKind.Square, new PdfRectF(30, 50, 170, 150), x => x with { Color = Red, BorderWidth = 2, CloudIntensity = 1 });
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        string target = P("cloudsq.pdf");
        await Svc().SaveAsync(s, target);

        // Square는 PDFium 길(Flush가 만든다) — 편집 중인 PDFium 문서에는 /BE가 없고, 저장 파일에만 패치로 들어간다.
        float inset = AnnotationGeometry.CloudRectInset(a.Rect, 1, 2);
        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(1, AnnotCount(doc));
            var d = AnnotDict(doc, 0);
            Assert.Equal("/Square", d.Elements.GetName("/Subtype"));
            var be = Deref<PdfDictionary>(d.Elements["/BE"]);
            Assert.Equal("/C", be.Elements.GetName("/S"));
            Assert.Equal(1.0, be.Elements["/I"] is PdfInteger ii ? ii.Value : ((PdfReal)be.Elements["/I"]!).Value, 3);
            Assert.Equal([(double)inset, inset, inset, inset], Nums(d, "/RD"), new Tol());
        }
        Assert.Equal([PdfAnnotSubtype.Square], await PdfiumSubtypes(target));
        await _t.InvokeAsync(() =>
        {
            Assert.Equal(1, s.GetLoadedPage(s.Pages[0].Id).AnnotationCount);
            using var live = s.GetLoadedPage(s.Pages[0].Id).GetAnnotation(0);
            Assert.False(live.HasKey("BE"));
        });

        var r = Assert.Single(ReadBack(target));
        Assert.Equal(ReadOnlyReason.None, r.Reason);
        Assert.Equal(AnnotKind.Square, r.Data!.Kind);
        Assert.Equal(1f, r.Data.CloudIntensity, 3);
        AssertRect(a.Rect, r.Data.Rect);
        Assert.Equal(2f, r.Data.BorderWidth);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task OriginalSquare_BeWithNonPositiveIntensity_Touched_BeAndRdRemoved(int intensity)
    {
        string src = P($"be{intensity}.pdf");
        TestPdfFactory.CreateBlank(src, 1, 200, 200);
        using (var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify))
        {
            var d = new PdfDictionary(doc);
            d.Elements["/Type"] = new PdfName("/Annot");
            d.Elements["/Subtype"] = new PdfName("/Square");
            d.Elements["/Rect"] = new PdfArray(doc, new PdfReal(30), new PdfReal(50), new PdfReal(170), new PdfReal(150));
            d.Elements["/C"] = new PdfArray(doc, new PdfReal(1), new PdfReal(0), new PdfReal(0));
            d.Elements["/F"] = new PdfInteger(4);
            d.Elements["/NM"] = new PdfString("sq-be");
            var be = new PdfDictionary(doc);
            be.Elements["/S"] = new PdfName("/C");
            be.Elements["/I"] = new PdfInteger(intensity);
            d.Elements["/BE"] = be;
            doc.Internals.AddObject(d);
            doc.Pages[0].Elements["/Annots"] = new PdfArray(doc, d.Reference!);
            doc.Save(src);
        }
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        await s.AnnotationsReady;
        var before = Assert.Single(s.Annotations.Get(s.Pages[0].Id).Items).Data;
        Assert.Equal(0f, before.CloudIntensity);
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(s.Pages[0].Id, [(before, before with { Color = Blue })]));
        string target = P($"be{intensity}-out.pdf");
        await Svc().SaveAsync(s, target);

        using var back = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var saved = AnnotDict(back, 0);
        Assert.False(saved.Elements.ContainsKey("/BE"));
        Assert.False(saved.Elements.ContainsKey("/RD"));
    }

    [Fact]
    public async Task CloudPolygon_SaveReopen()
    {
        await using var s = await OpenBlankAsync();
        var a = Make(s, AnnotKind.Polygon, default, x => x with
        {
            Color = Blue, FillColor = Yellow, BorderWidth = 2, CloudIntensity = 1.5f,
            Vertices = [new PdfPointF(50, 60), new PdfPointF(40, 140), new PdfPointF(150, 150), new PdfPointF(160, 80)],
        });
        a = a with { Rect = AnnotationGeometry.ComputeRect(a) };
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        string target = P("cloudpg.pdf");
        await Svc().SaveAsync(s, target);

        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            var be = Deref<PdfDictionary>(AnnotDict(doc, 0).Elements["/BE"]);
            Assert.Equal("/C", be.Elements.GetName("/S"));
        }
        var r = Assert.Single(ReadBack(target));
        Assert.Equal(ReadOnlyReason.None, r.Reason);
        Assert.Equal(1.5f, r.Data!.CloudIntensity, 3);
        AssertPoints(a.Vertices, r.Data.Vertices);
        AssertRect(a.Rect, r.Data.Rect);
        Assert.Equal([PdfAnnotSubtype.Polygon], await PdfiumSubtypes(target));
    }

    [Fact]
    public async Task Caret_SaveReopen()
    {
        await using var s = await OpenBlankAsync();
        var a = Make(s, AnnotKind.Caret, new PdfRectF(85, 85, 115, 115), x => x with { Color = Red, Contents = "여기에 넣기" });
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        string target = P("caret.pdf");
        await Svc().SaveAsync(s, target);

        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            var d = AnnotDict(doc, 0);
            Assert.Equal("/Caret", d.Elements.GetName("/Subtype"));
            Assert.Equal("/None", d.Elements.GetName("/Sy"));
            Assert.Equal([0d, 0, 0, 0], Nums(d, "/RD"));
        }
        var r = Assert.Single(ReadBack(target));
        Assert.Equal(ReadOnlyReason.None, r.Reason);
        Assert.Equal(AnnotKind.Caret, r.Data!.Kind);
        Assert.Equal("여기에 넣기", r.Data.Contents);
        Assert.Equal(Red, r.Data.Color);
        AssertRect(a.Rect, r.Data.Rect);
        Assert.Equal([PdfAnnotSubtype.Caret], await PdfiumSubtypes(target));
    }

    /// <summary>다른 프로그램이 만든 Line(모르는 키 /Foo 포함, /LL 없음)을 파일로 만든다.</summary>
    private string MakeOriginalLine()
    {
        string p = P($"orig-{Guid.NewGuid():N}.pdf");
        TestPdfFactory.CreateBlank(p, 1, 200, 200);
        using (var doc = PdfReader.Open(p, PdfDocumentOpenMode.Modify))
        {
            var d = new PdfDictionary(doc);
            d.Elements["/Type"] = new PdfName("/Annot");
            d.Elements["/Subtype"] = new PdfName("/Line");
            d.Elements["/Rect"] = Arr(doc, 20, 20, 120, 80);
            d.Elements["/L"] = Arr(doc, 30, 30, 110, 70);
            var le = new PdfArray(doc);
            le.Elements.Add(new PdfName("/None"));
            le.Elements.Add(new PdfName("/OpenArrow"));
            d.Elements["/LE"] = le;
            d.Elements["/C"] = Arr(doc, 0, 0, 1);
            d.Elements["/Border"] = Arr(doc, 0, 0, 2);
            d.Elements["/F"] = new PdfInteger(4);
            d.Elements["/NM"] = new PdfString("orig-line");
            d.Elements["/Foo"] = new PdfName("/Bar");
            doc.Internals.AddObject(d);
            var arr = new PdfArray(doc);
            arr.Elements.Add(d.Reference!);
            doc.Pages[0].Elements["/Annots"] = arr;
            doc.Save(p);
        }
        return p;
    }

    private static PdfArray Arr(PdfSharp.Pdf.PdfDocument doc, params double[] v)
    {
        var a = new PdfArray(doc);
        foreach (var x in v) a.Elements.Add(new PdfReal(x));
        return a;
    }

    [Fact]
    public async Task TouchedOriginalLine_UnknownKeysKept()
    {
        string pdf = MakeOriginalLine();
        await using var s = await DocumentSession.OpenAsync(_t, pdf, null);
        await s.AnnotationsReady;
        var id = s.Pages[0].Id;
        var before = Assert.Single(s.Annotations.Get(id).Items).Data;
        Assert.Equal(AnnotKind.Line, before.Kind);
        Assert.Equal(LineEnding.OpenArrow, before.EndEnding);

        var moved = before with
        {
            Vertices = [new PdfPointF(40, 40), new PdfPointF(100, 60)], Color = Red, EndEnding = LineEnding.ClosedArrow, StartEnding = LineEnding.Square,
        };
        moved = moved with { Rect = AnnotationGeometry.ComputeRect(moved) };
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(before, moved)]));
        string target = P("touched-line.pdf");
        await Svc().SaveAsync(s, target);

        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(1, AnnotCount(doc));   // 같은 자리에서 고쳤다(복제 없음)
            var d = AnnotDict(doc, 0);
            Assert.Equal("/Bar", d.Elements.GetName("/Foo"));   // 모르는 키 보존
            Assert.Equal("orig-line", d.Elements.GetString("/NM"));
            Assert.Equal([40d, 40, 100, 60], Nums(d, "/L"));
        }
        var r = Assert.Single(ReadBack(target)).Data!;
        Assert.Equal(LineEnding.Square, r.StartEnding);
        Assert.Equal(LineEnding.ClosedArrow, r.EndEnding);
        Assert.Equal(Red, r.Color);
        AssertRect(moved.Rect, r.Rect);
        Assert.Equal([PdfAnnotSubtype.Line], await PdfiumSubtypes(target));

        // 두 번 더 저장해도 겹치지 않는다.
        await Svc().SaveAsync(s, target);
        using var again = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.Equal(1, AnnotCount(again));
    }

    [Fact]
    public async Task DeletedNewLineAfterSave_NotInNextSave()
    {
        await using var s = await OpenBlankAsync();
        var id = s.Pages[0].Id;
        var a = Arrow(s);
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, a));
        string target = P("del.pdf");
        await Svc().SaveAsync(s, target);
        Assert.Single(ReadBack(target));

        // 저장 뒤에도 새 선은 PDFium 문서에 없다(번호 없음). 지우고 다시 저장하면 파일에서 사라진다.
        await _t.InvokeAsync(() => Assert.Equal(0, s.GetLoadedPage(id).AnnotationCount));
        var cur = Assert.Single(s.Annotations.Get(id).Items).Data;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Delete(id, [cur]));
        await Svc().SaveAsync(s, target);
        Assert.Empty(ReadBack(target));
        Assert.Empty(await PdfiumSubtypes(target));

        // 실행 취소하면 다시 저장 파일에 나타난다.
        await s.Commands.UndoAsync();
        await Svc().SaveAsync(s, target);
        Assert.Single(ReadBack(target));
    }

    [Fact]
    public async Task TouchedOriginalLine_OpacityKept_AfterSavingTwice()
    {
        string pdf = MakeOriginalLine();
        await using var s = await DocumentSession.OpenAsync(_t, pdf, null);
        await s.AnnotationsReady;
        var id = s.Pages[0].Id;
        var before = Assert.Single(s.Annotations.Get(id).Items).Data;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(before, before with { Opacity = 0.5f })]));
        string target = P("opacity.pdf");
        for (int i = 0; i < 2; i++)
        {
            await Svc().SaveAsync(s, target);
            using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
            Assert.Equal(1, AnnotCount(doc));
            var d = AnnotDict(doc, 0);
            Assert.Equal(0.5, ((PdfReal)d.Elements["/CA"]!).Value, 3);
            var form = Deref<PdfDictionary>(Deref<PdfDictionary>(d.Elements["/AP"]).Elements["/N"]);
            var gs = Deref<PdfDictionary>(Deref<PdfDictionary>(Deref<PdfDictionary>(form.Elements["/Resources"]).Elements["/ExtGState"]).Elements["/GS"]);
            Assert.Equal(0.5, ((PdfReal)gs.Elements["/CA"]!).Value, 3);
            Assert.Equal("/Bar", d.Elements.GetName("/Foo"));
        }
        Assert.Equal(0.5f, Assert.Single(ReadBack(target)).Data!.Opacity, 3);
    }

    [Fact]
    public async Task TouchedOriginalCaret_SyKept()
    {
        string p = P("caret-orig.pdf");
        TestPdfFactory.CreateBlank(p, 1, 200, 200);
        using (var doc = PdfReader.Open(p, PdfDocumentOpenMode.Modify))
        {
            var d = new PdfDictionary(doc);
            d.Elements["/Type"] = new PdfName("/Annot");
            d.Elements["/Subtype"] = new PdfName("/Caret");
            d.Elements["/Rect"] = Arr(doc, 50, 50, 70, 70);
            d.Elements["/Sy"] = new PdfName("/P");
            d.Elements["/F"] = new PdfInteger(4);
            d.Elements["/NM"] = new PdfString("orig-caret");
            doc.Internals.AddObject(d);
            var arr = new PdfArray(doc);
            arr.Elements.Add(d.Reference!);
            doc.Pages[0].Elements["/Annots"] = arr;
            doc.Save(p);
        }
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.AnnotationsReady;
        var id = s.Pages[0].Id;
        var before = Assert.Single(s.Annotations.Get(id).Items).Data;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(before, before with { Color = Red })]));
        string target = P("caret-touched.pdf");
        await Svc().SaveAsync(s, target);
        using var saved = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.Equal("/P", AnnotDict(saved, 0).Elements.GetName("/Sy"));
        Assert.Equal(Red, Assert.Single(ReadBack(target)).Data!.Color);
    }

    [Fact]
    public async Task MixedPage_PdfiumKindsAndPatchLine_CountsAndIndices()
    {
        AnnotatedPdfFactory.CreateMixed(P("mixed.pdf"));
        await using var s = await DocumentSession.OpenAsync(_t, P("mixed.pdf"), null);
        await s.AnnotationsReady;
        var id = s.Pages[0].Id;
        var origSubtypes = await PdfiumSubtypes(P("mixed.pdf"));
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, Arrow(s)));
        // PDFium 길 새 주석(Square, 메모)도 같은 쪽에 더한다.
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, Make(s, AnnotKind.Square, new PdfRectF(30, 30, 90, 80), x => x with { Color = Blue, BorderWidth = 2 })));
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, Make(s, AnnotKind.Text, new PdfRectF(150, 150, 170, 170), x => x with { Color = Yellow, Contents = "memo" })));

        string target = P("mixed-out.pdf");
        for (int i = 0; i < 2; i++)
        {
            await Svc().SaveAsync(s, target);
            var subs = await PdfiumSubtypes(target);
            // 원본 13개는 같은 번호·같은 유형으로 앞에, 새 주석(Square, Text, Popup, Line)이 뒤에 붙는다.
            Assert.Equal(origSubtypes, subs.Take(origSubtypes.Count).ToList());
            var tail = subs.Skip(origSubtypes.Count).ToList();
            Assert.Equal(4, tail.Count);
            Assert.Single(tail, t => t == PdfAnnotSubtype.Line);
            Assert.Single(tail, t => t == PdfAnnotSubtype.Popup);
            Assert.Single(tail, t => t == PdfAnnotSubtype.Square);
            Assert.Single(tail, t => t == PdfAnnotSubtype.Text);
            Assert.Equal(1, ReadBack(target).Count(o => o.Data?.Kind == AnnotKind.Line));
        }
    }

    private sealed class Tol : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 0.01;
        public int GetHashCode(double v) => 0;
    }
}
