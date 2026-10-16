using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Pdf.Native.Tests;

/// <summary>Pdf.Native 주석 API(M2 Task 1). 확인이 필요하면 SaveCopy 후 PDFsharp로 원시 딕셔너리를 읽는다.</summary>
public class AnnotationApiTests : IDisposable
{
    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-annot-" + Guid.NewGuid().ToString("N"));

    public AnnotationApiTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private Pdf.Native.PdfDocument NewDoc()
    {
        var doc = Pdf.Native.PdfDocument.CreateNew(_t);
        doc.AddPage(595, 842);
        return doc;
    }

    // ---- PDFsharp 헬퍼 ----
    private static PdfDictionary Deref(PdfItem? item) => item switch
    {
        PdfReference r => (PdfDictionary)r.Value,
        PdfDictionary d => d,
        _ => throw new InvalidOperationException("딕셔너리가 아님: " + item),
    };

    private static T WithAnnot<T>(string path, int index, Func<PdfDictionary, T> f)
    {
        using var d = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var annots = d.Pages[0].Elements.GetArray("/Annots")!;
        return f(Deref(annots.Elements[index]));
    }

    private static PdfDictionary NewAnnot(PdfSharp.Pdf.PdfDocument d, string subtype, double l, double b, double r, double t)
    {
        var a = new PdfDictionary(d);
        a.Elements["/Type"] = new PdfName("/Annot");
        a.Elements["/Subtype"] = new PdfName("/" + subtype);
        var rect = new PdfArray(d);
        foreach (var v in new[] { l, b, r, t }) rect.Elements.Add(new PdfReal(v));
        a.Elements["/Rect"] = rect;
        d.Internals.AddObject(a);
        return a;
    }

    private string MakePdfsharpAnnots(string name, Action<PdfSharp.Pdf.PdfDocument, List<PdfDictionary>> build)
    {
        var path = P(name);
        using var d = new PdfSharp.Pdf.PdfDocument();
        var pg = d.AddPage();
        pg.Width = PdfSharp.Drawing.XUnit.FromPoint(595);
        pg.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
        var list = new List<PdfDictionary>();
        build(d, list);
        var arr = new PdfArray(d);
        foreach (var a in list) arr.Elements.Add(a.Reference!);
        pg.Elements["/Annots"] = arr;
        d.Save(path);
        return path;
    }

    // ---- 테스트 ----
    [Fact]
    public async Task CreateAnnotation_AppendsAtEnd_SubtypeAndRect()
    {
        var (count, subtype, rect) = await _t.InvokeAsync(() =>
        {
            using var doc = NewDoc();
            using var page = doc.LoadPage(0);
            using (var a = page.CreateAnnotation(PdfAnnotSubtype.Square))
                a.Rect = new PdfRectF(100, 600, 300, 650);
            using var back = page.GetAnnotation(0);
            return (page.AnnotationCount, back.Subtype, back.Rect);
        });
        Assert.Equal(1, count);
        Assert.Equal(5, subtype);
        Assert.Equal(new PdfRectF(100, 600, 300, 650), rect);
    }

    [Fact]
    public async Task RemoveAnnotation_ShiftsLaterIndices()
    {
        var subtypes = await _t.InvokeAsync(() =>
        {
            using var doc = NewDoc();
            using var page = doc.LoadPage(0);
            page.CreateAnnotation(PdfAnnotSubtype.Square).Dispose();
            page.CreateAnnotation(PdfAnnotSubtype.Circle).Dispose();
            page.CreateAnnotation(PdfAnnotSubtype.Ink).Dispose();
            page.RemoveAnnotation(0);
            var list = new List<int>();
            for (int i = 0; i < page.AnnotationCount; i++)
            {
                using var a = page.GetAnnotation(i);
                list.Add(a.Subtype);
            }
            return list;
        });
        Assert.Equal([PdfAnnotSubtype.Circle, PdfAnnotSubtype.Ink], subtypes);
    }

    [Fact]
    public async Task Strings_RoundTrip_AndNameObjectsReadable()
    {
        var path = MakePdfsharpAnnots("name.pdf", (d, l) =>
        {
            var a = NewAnnot(d, "Text", 50, 50, 70, 70);
            a.Elements["/Name"] = new PdfName("/Comment");
            l.Add(a);
        });
        var r = await _t.InvokeAsync(() =>
        {
            using var doc = NewDoc();
            using var page = doc.LoadPage(0);
            using var c = page.CreateAnnotation(PdfAnnotSubtype.Text);
            c.SetString("T", "검토자");
            using var doc2 = Pdf.Native.PdfDocument.Open(_t, path);
            using var p2 = doc2.LoadPage(0);
            using var b = p2.GetAnnotation(0);
            return (T: c.GetString("T"), HasT: c.HasKey("T"), HasX: c.HasKey("Nope"), Missing: c.GetString("Nope"),
                    Name: b.GetString("Name"), MissingB: b.GetString("Nope"));
        });
        Assert.Equal("검토자", r.T);
        Assert.True(r.HasT);
        Assert.False(r.HasX);
        Assert.Null(r.Missing);
        Assert.Equal("Comment", r.Name);
        Assert.Null(r.MissingB);
    }

    [Fact]
    public async Task SetColor_WhenApExists_Throws_AfterClearingAp_Works()
    {
        var path = P("color.pdf");
        await _t.InvokeAsync(() =>
        {
            using var doc = NewDoc();
            using var page = doc.LoadPage(0);
            using var a = page.CreateAnnotation(PdfAnnotSubtype.Square);
            a.Rect = new PdfRectF(100, 600, 300, 650);
            a.SetAppearance("0 g 0 0 1 1 re f");
            Assert.Throws<InvalidOperationException>(() => a.SetColor(PdfColorType.Color, 255, 0, 0, 0.5f));
            a.SetAppearance(null);
            Assert.Null(a.GetAppearance());
            a.SetColor(PdfColorType.Color, 255, 0, 0, 0.5f);
            doc.SaveCopy(path);
        });
        var (c, ca) = WithAnnot(path, 0, d =>
        {
            var arr = d.Elements.GetArray("/C")!;
            return ((arr.Elements.GetReal(0), arr.Elements.GetReal(1), arr.Elements.GetReal(2)), d.Elements.GetReal("/CA"));
        });
        Assert.Equal((1.0, 0.0, 0.0), c);
        Assert.InRange(ca, 0.49, 0.51);
    }

    [Fact]
    public async Task Quads_RoundTrip_AndCannotShrink()
    {
        var q1 = new PdfQuadF(new(10, 50), new(110, 50), new(10, 30), new(110, 30));
        var q2 = new PdfQuadF(new(10, 100), new(90, 100), new(10, 80), new(90, 80));
        var (got, shrinkThrows, grown) = await _t.InvokeAsync(() =>
        {
            using var doc = NewDoc();
            using var page = doc.LoadPage(0);
            using var a = page.CreateAnnotation(PdfAnnotSubtype.Highlight);
            a.SetQuads([q1, q2]);
            var g = a.GetQuads();
            bool throws = false;
            try { a.SetQuads([q1]); } catch (NotSupportedException) { throws = true; }
            a.SetQuads([q2, q1, q2]);
            return (g, throws, a.GetQuads());
        });
        Assert.Equal([q1, q2], got);
        Assert.True(shrinkThrows);
        Assert.Equal([q2, q1, q2], grown);
    }

    [Fact]
    public async Task InkStrokes_RoundTrip()
    {
        IReadOnlyList<IReadOnlyList<PdfPointF>> strokes =
        [
            [new(10, 10), new(20, 30), new(40, 35)],
            [new(50, 50), new(60, 60)],
        ];
        var got = await _t.InvokeAsync(() =>
        {
            using var doc = NewDoc();
            using var page = doc.LoadPage(0);
            using var a = page.CreateAnnotation(PdfAnnotSubtype.Ink);
            a.SetInkStrokes(strokes);
            return a.GetInkStrokes();
        });
        Assert.Equal(2, got.Count);
        Assert.Equal(strokes[0], got[0]);
        Assert.Equal(strokes[1], got[1]);
    }

    [Fact]
    public async Task LinkedPopupIndex_Found()
    {
        var path = MakePdfsharpAnnots("popup.pdf", (d, l) =>
        {
            var text = NewAnnot(d, "Text", 50, 50, 70, 70);
            var popup = NewAnnot(d, "Popup", 100, 100, 200, 200);
            popup.Elements["/Parent"] = text.Reference!;
            text.Elements["/Popup"] = popup.Reference!;
            l.Add(text);
            l.Add(popup);
        });
        var (found, none) = await _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            return (page.GetLinkedAnnotationIndex(0, "Popup"), page.GetLinkedAnnotationIndex(1, "Popup"));
        });
        Assert.Equal(1, found);
        Assert.Null(none);
    }

    [Fact]
    public async Task AnnotIndex_MatchesAnnotsArrayOrder()
    {
        var path = MakePdfsharpAnnots("order.pdf", (d, l) =>
        {
            l.Add(NewAnnot(d, "Link", 10, 10, 50, 30));
            l.Add(NewAnnot(d, "Square", 60, 10, 100, 30));
            l.Add(NewAnnot(d, "Text", 110, 10, 130, 30));
            l.Add(NewAnnot(d, "Popup", 140, 10, 200, 60));
        });
        var subtypes = await _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            var list = new List<int>();
            for (int i = 0; i < page.AnnotationCount; i++)
            {
                using var a = page.GetAnnotation(i);
                list.Add(a.Subtype);
            }
            return list;
        });
        Assert.Equal([2, 5, 1, 16], subtypes);
    }

    [Fact]
    public async Task HiddenAnnotIndices_HidesOnlyThoseAnnots()
    {
        var (hiddenFirst, hiddenSecond, normalFirst, flagsRestored) = await _t.InvokeAsync(() =>
        {
            using var doc = NewDoc();
            using var page = doc.LoadPage(0);
            using (var a = page.CreateAnnotation(PdfAnnotSubtype.Square))
            {
                a.Rect = new PdfRectF(100, 600, 200, 650);
                a.SetAppearance("1 0 0 rg 100 600 100 50 re f");
            }
            using (var a = page.CreateAnnotation(PdfAnnotSubtype.Square))
            {
                a.Rect = new PdfRectF(300, 600, 400, 650);
                a.SetAppearance("1 0 0 rg 300 600 100 50 re f");
            }
            var before = AnnotTestHelper.GetFlags(page);
            // 페이지 y 600~650 → 화면 y 192~242
            var normal = page.RenderTile(1.0, 0, 0, 595, 842, new RenderOptions(true));
            var hidden = page.RenderTile(1.0, 0, 0, 595, 842, new RenderOptions(true, null, new HashSet<int> { 0 }));
            var after = AnnotTestHelper.GetFlags(page);
            return (IsRed(hidden, 595, 150, 217), IsRed(hidden, 595, 350, 217), IsRed(normal, 595, 150, 217), before.SequenceEqual(after));
        });
        Assert.True(normalFirst, "기준: 0번 자리는 빨강이어야 함");
        Assert.False(hiddenFirst, "0번 자리는 숨겨져 흰색");
        Assert.True(hiddenSecond, "1번 자리는 그대로 빨강");
        Assert.True(flagsRestored);
    }

    private static bool IsRed(byte[] bgra, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
        return r > 180 && g < 90 && b < 90;
    }
}
