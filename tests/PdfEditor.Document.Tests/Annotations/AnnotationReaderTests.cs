using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests.Annotations;

public class AnnotationReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-areader-" + Guid.NewGuid().ToString("N"));

    public AnnotationReaderTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private IReadOnlyList<OriginalAnnotation> ReadMixed()
    {
        var p = P("mixed.pdf");
        AnnotatedPdfFactory.CreateMixed(p);
        return AnnotationReader.Read(File.ReadAllBytes(p), null, [0])[0];
    }

    [Fact]
    public void Read_Mixed_ClassifiesEachIndex()
    {
        var r = ReadMixed();
        Assert.Equal(13, r.Count);
        ReadOnlyReason[] expected =
        [
            ReadOnlyReason.UnsupportedType, ReadOnlyReason.None, ReadOnlyReason.None, ReadOnlyReason.None,
            ReadOnlyReason.UnsupportedType, ReadOnlyReason.Reply, ReadOnlyReason.None,
            ReadOnlyReason.None, ReadOnlyReason.None, ReadOnlyReason.None, ReadOnlyReason.None,
            ReadOnlyReason.None, ReadOnlyReason.None,
        ];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(i, r[i].Index);
            Assert.Equal(expected[i], r[i].Reason);
            Assert.Equal(expected[i] == ReadOnlyReason.None, r[i].Data is not null);
        }
    }

    [Fact]
    public void Read_Square_ColorWidthOpacityFromBsAndCa()
    {
        var a = ReadMixed()[1].Data!;
        Assert.Equal(AnnotKind.Square, a.Kind);
        Assert.Equal("#FF0000", a.Color!.Value.ToHex());
        Assert.Equal(3f, a.BorderWidth);
        Assert.Equal(0.8f, a.Opacity, 3);
        Assert.Null(a.FillColor);
        Assert.Equal(new PdfRectF(100, 600, 300, 650), a.Rect);
        Assert.Equal(default, a.Id);
    }

    [Fact]
    public void Read_Ink_StrokesAndBorderArrayWidth()
    {
        var a = ReadMixed()[7].Data!;
        Assert.Equal(AnnotKind.Ink, a.Kind);
        Assert.Equal(2, a.InkStrokes.Length);
        Assert.Equal([new PdfPointF(100, 400), new PdfPointF(160, 480)], a.InkStrokes[0].ToArray());
        Assert.Equal([new PdfPointF(160, 400), new PdfPointF(220, 480)], a.InkStrokes[1].ToArray());
        Assert.Equal(2f, a.BorderWidth);
    }

    [Fact]
    public void Read_Highlight_QuadsInP1ToP4Order()
    {
        var a = ReadMixed()[2].Data!;
        Assert.Equal(AnnotKind.Highlight, a.Kind);
        Assert.Equal("#FFFF00", a.Color!.Value.ToHex());
        Assert.Equal(0.5f, a.Opacity, 3);
        Assert.Equal(2, a.Quads.Length);
        var q = a.Quads[0];
        Assert.Equal(new PdfPointF(100, 715), q.P1);
        Assert.Equal(new PdfPointF(300, 715), q.P2);
        Assert.Equal(new PdfPointF(100, 700), q.P3);
        Assert.Equal(new PdfPointF(300, 700), q.P4);
        Assert.Equal(new PdfPointF(250, 680), a.Quads[1].P4);
    }

    [Fact]
    public void Read_Text_ContentsAuthorNameDates()
    {
        var a = ReadMixed()[3].Data!;
        Assert.Equal(AnnotKind.Text, a.Kind);
        Assert.Equal(AnnotatedPdfFactory.MemoText, a.Contents);
        Assert.Equal(AnnotatedPdfFactory.ReviewerName, a.Author);
        Assert.True(PdfEditor.Document.Annotations.PdfDate.TryParse(AnnotatedPdfFactory.ModifiedDate, out var expected));
        Assert.Equal(expected, a.ModifiedAt);
        Assert.Null(a.CreatedAt);
        Assert.Equal(4, a.Flags);
    }

    [Fact]
    public void Read_GrayAndCmyk_ConvertedToRgb()
    {
        var r = ReadMixed();
        var gray = r[11].Data!.Color!.Value;
        Assert.InRange(gray.R, 127, 129);
        Assert.Equal(gray.R, gray.G);
        Assert.Equal(gray.R, gray.B);
        Assert.Equal("#FF0000", r[12].Data!.Color!.Value.ToHex());
    }

    [Fact]
    public void Read_CircleInteriorColor()
    {
        var a = ReadMixed()[10].Data!;
        Assert.Equal(AnnotKind.Circle, a.Kind);
        Assert.Equal("#00994D", a.FillColor!.Value.ToHex());
        Assert.Equal("#0000FF", a.Color!.Value.ToHex());
    }

    [Fact]
    public async Task Read_IndicesMatchPdfium()
    {
        var p = P("mixed.pdf");
        AnnotatedPdfFactory.CreateMixed(p);
        var mine = AnnotationReader.Read(File.ReadAllBytes(p), null, [0])[0];

        using var t = new PdfiumThread();
        var fromPdfium = await t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(t, p);
            using var page = doc.LoadPage(0);
            var list = new List<(int Subtype, PdfRectF Rect)>();
            for (int i = 0; i < page.AnnotationCount; i++)
            {
                using var a = page.GetAnnotation(i);
                list.Add((a.Subtype, a.Rect));
            }
            return list;
        });

        Assert.Equal(mine.Count, fromPdfium.Count);
        for (int i = 0; i < mine.Count; i++)
        {
            Assert.Equal(fromPdfium[i].Subtype, mine[i].Subtype);
            if (mine[i].Data is { } d)
            {
                var pr = fromPdfium[i].Rect;
                Assert.InRange(Math.Abs(d.Rect.Left - pr.Left), 0, 0.01);
                Assert.InRange(Math.Abs(d.Rect.Bottom - pr.Bottom), 0, 0.01);
                Assert.InRange(Math.Abs(d.Rect.Right - pr.Right), 0, 0.01);
                Assert.InRange(Math.Abs(d.Rect.Top - pr.Top), 0, 0.01);
            }
        }
    }

    [Fact]
    public void Read_MalformedEntries_MarkedNotThrown()
    {
        var p = P("malformed.pdf");
        using (var doc = new PdfSharp.Pdf.PdfDocument())
        {
            var page = doc.AddPage();
            var good = new PdfDictionary(doc);
            good.Elements["/Type"] = new PdfName("/Annot");
            good.Elements["/Subtype"] = new PdfName("/Square");
            good.Elements["/Rect"] = new PdfArray(doc, new PdfReal(10), new PdfReal(10), new PdfReal(50), new PdfReal(50));
            doc.Internals.AddObject(good);
            var noRect = new PdfDictionary(doc);
            noRect.Elements["/Type"] = new PdfName("/Annot");
            noRect.Elements["/Subtype"] = new PdfName("/Square");
            doc.Internals.AddObject(noRect);
            var noInk = new PdfDictionary(doc);
            noInk.Elements["/Subtype"] = new PdfName("/Ink");
            noInk.Elements["/Rect"] = new PdfArray(doc, new PdfReal(10), new PdfReal(10), new PdfReal(50), new PdfReal(50));
            doc.Internals.AddObject(noInk);
            var noQuads = new PdfDictionary(doc);
            noQuads.Elements["/Subtype"] = new PdfName("/Underline");
            noQuads.Elements["/Rect"] = new PdfArray(doc, new PdfReal(10), new PdfReal(10), new PdfReal(50), new PdfReal(50));
            doc.Internals.AddObject(noQuads);

            page.Elements["/Annots"] = new PdfArray(doc,
                PdfSharp.Pdf.PdfNull.Value, new PdfInteger(7), noRect.Reference!, good.Reference!, noInk.Reference!, noQuads.Reference!);
            doc.Save(p);
        }

        var r = AnnotationReader.Read(File.ReadAllBytes(p), null, [0])[0];
        Assert.Equal(6, r.Count);
        Assert.Equal(ReadOnlyReason.Malformed, r[0].Reason);
        Assert.Equal(ReadOnlyReason.Malformed, r[1].Reason);
        Assert.Equal(ReadOnlyReason.Malformed, r[2].Reason);
        Assert.Equal(ReadOnlyReason.None, r[3].Reason);
        Assert.Equal(ReadOnlyReason.Malformed, r[4].Reason);
        Assert.Equal(ReadOnlyReason.Malformed, r[5].Reason);
    }

    [Fact]
    public void Read_WrongPassword_ThrowsAnnotationReadException()
    {
        var p = P("enc.pdf");
        AnnotatedPdfFactory.CreateEncryptedWithAnnotations(p, "secret");
        var bytes = File.ReadAllBytes(p);
        Assert.Throws<AnnotationReadException>(() => AnnotationReader.Read(bytes, "wrong", [0]));
    }

    [Fact]
    public void Read_TestFailure_IsWrapped()
    {
        var p = P("mixed.pdf");
        AnnotatedPdfFactory.CreateMixed(p);
        var bytes = File.ReadAllBytes(p);
        int before = AnnotationReader.OpenCount;
        AnnotationReader.Read(bytes, null, [0]);
        Assert.Equal(before + 1, AnnotationReader.OpenCount);

        AnnotationReader.TestFailure = new InvalidOperationException("boom");
        try
        {
            var ex = Assert.Throws<AnnotationReadException>(() => AnnotationReader.Read(bytes, null, [0]));
            Assert.IsType<InvalidOperationException>(ex.InnerException);
        }
        finally { AnnotationReader.TestFailure = null; }
    }

    // 관찰 결과(2026-10-16, PDFsharp 6.2.4 Import 모드): 올바른 사용자 암호면 열리고 원본 주석을 읽는다.
    // 암호를 안 주면 PdfReaderException(→ AnnotationReadException)으로 열리지 않는다.
    [Fact]
    public void Read_EncryptedWithUserPassword_ProbeResult()
    {
        var p = P("enc.pdf");
        AnnotatedPdfFactory.CreateEncryptedWithAnnotations(p, "secret");
        var bytes = File.ReadAllBytes(p);

        var r = AnnotationReader.Read(bytes, "secret", [0])[0];
        Assert.Equal(3, r.Count);
        Assert.Equal(ReadOnlyReason.None, r[0].Reason);
        Assert.Equal(AnnotKind.Square, r[0].Data!.Kind);
        Assert.Equal(ReadOnlyReason.None, r[1].Reason);
        Assert.Equal(AnnotatedPdfFactory.MemoText, r[1].Data!.Contents);
        Assert.Equal(ReadOnlyReason.UnsupportedType, r[2].Reason);

        var ex = Assert.Throws<AnnotationReadException>(() => AnnotationReader.Read(bytes, null, [0]));
        Assert.IsType<PdfReaderException>(ex.InnerException);
    }

    // 관찰 결과: 소유자 암호만 있는 문서는 주석 권한이 꺼져 있어도 읽힌다 (읽기는 권한과 무관).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_OwnerRestrictedNoUserPassword_ProbeResult(bool allowAnnotations)
    {
        var p = P($"owner-{allowAnnotations}.pdf");
        AnnotatedPdfFactory.CreateOwnerRestrictedWithAnnotations(p, allowAnnotations);
        var r = AnnotationReader.Read(File.ReadAllBytes(p), null, [0])[0];
        Assert.Single(r);
        Assert.Equal(ReadOnlyReason.None, r[0].Reason);
        Assert.Equal(AnnotKind.Square, r[0].Data!.Kind);
    }

    // ---- Task 9: 선·폴리라인·다각형·클라우드·Caret ----

    private IReadOnlyList<OriginalAnnotation> ReadWith(Action<PdfSharp.Pdf.PdfDocument, PdfSharp.Pdf.PdfPage> build)
    {
        var p = P($"m3-{Guid.NewGuid():N}.pdf");
        using (var doc = new PdfSharp.Pdf.PdfDocument())
        {
            var page = doc.AddPage();
            build(doc, page);
            doc.Save(p);
        }
        return AnnotationReader.Read(File.ReadAllBytes(p), null, [0])[0];
    }

    private static PdfArray Nums(PdfSharp.Pdf.PdfDocument doc, params double[] v)
    {
        var a = new PdfArray(doc);
        foreach (var x in v) a.Elements.Add(new PdfReal(x));
        return a;
    }

    private static PdfDictionary Add(PdfSharp.Pdf.PdfDocument doc, PdfSharp.Pdf.PdfPage page, string subtype, params double[] rect)
    {
        var d = new PdfDictionary(doc);
        d.Elements["/Type"] = new PdfName("/Annot");
        d.Elements["/Subtype"] = new PdfName("/" + subtype);
        d.Elements["/Rect"] = Nums(doc, rect);
        doc.Internals.AddObject(d);
        var arr = page.Elements["/Annots"] as PdfArray ?? new PdfArray(doc);
        arr.Elements.Add(d.Reference!);
        page.Elements["/Annots"] = arr;
        return d;
    }

    private static PdfArray Names(PdfSharp.Pdf.PdfDocument doc, params string[] names)
    {
        var a = new PdfArray(doc);
        foreach (var n in names) a.Elements.Add(new PdfName(n));
        return a;
    }

    [Fact]
    public void Read_Line_PointsAndEndings()
    {
        var r = ReadWith((doc, page) =>
        {
            var d = Add(doc, page, "Line", 10, 10, 110, 60);
            d.Elements["/L"] = Nums(doc, 20, 20, 100, 50);
            d.Elements["/LE"] = Names(doc, "/Square", "/ClosedArrow");
            d.Elements["/IC"] = Nums(doc, 1, 0, 0);
        });
        var o = Assert.Single(r);
        Assert.Equal(ReadOnlyReason.None, o.Reason);
        var a = o.Data!;
        Assert.Equal(AnnotKind.Line, a.Kind);
        Assert.Equal([new PdfPointF(20, 20), new PdfPointF(100, 50)], a.Vertices.ToArray());
        Assert.Equal(LineEnding.Square, a.StartEnding);
        Assert.Equal(LineEnding.ClosedArrow, a.EndEnding);
        Assert.Equal("#FF0000", a.FillColor!.Value.ToHex());
    }

    [Fact]
    public void Read_UnknownLineEnding_ReadOnlyUnsupportedLineEnding()
    {
        var r = ReadWith((doc, page) =>
        {
            var d = Add(doc, page, "Line", 10, 10, 110, 60);
            d.Elements["/L"] = Nums(doc, 20, 20, 100, 50);
            d.Elements["/LE"] = Names(doc, "/None", "/ROpenArrow");
            var p = Add(doc, page, "PolyLine", 10, 10, 110, 60);
            p.Elements["/Vertices"] = Nums(doc, 20, 20, 60, 50, 100, 20);
            p.Elements["/LE"] = Names(doc, "/Butt", "/Slash");
        });
        Assert.Equal(ReadOnlyReason.UnsupportedLineEnding, r[0].Reason);
        Assert.Null(r[0].Data);
        Assert.Equal(ReadOnlyReason.None, r[1].Reason);
        Assert.Equal(AnnotKind.PolyLine, r[1].Data!.Kind);
        Assert.Equal(3, r[1].Data!.Vertices.Length);
        Assert.Equal(LineEnding.Butt, r[1].Data!.StartEnding);
        Assert.Equal(LineEnding.Slash, r[1].Data!.EndEnding);
    }

    [Fact]
    public void Read_PolygonCloud_AndBrokenVertices()
    {
        var r = ReadWith((doc, page) =>
        {
            var d = Add(doc, page, "Polygon", 10, 10, 110, 60);
            d.Elements["/Vertices"] = Nums(doc, 20, 20, 100, 20, 60, 50);
            var be = new PdfDictionary(doc);
            be.Elements["/S"] = new PdfName("/C");
            be.Elements["/I"] = new PdfInteger(2);
            d.Elements["/BE"] = be;
            var bad = Add(doc, page, "Polygon", 10, 10, 110, 60);
            bad.Elements["/Vertices"] = Nums(doc, 20, 20, 100, 20);   // 점이 둘뿐
            var odd = Add(doc, page, "Line", 10, 10, 110, 60);
            odd.Elements["/L"] = Nums(doc, 20, 20, 100);
        });
        Assert.Equal(ReadOnlyReason.None, r[0].Reason);
        Assert.Equal(2f, r[0].Data!.CloudIntensity);
        Assert.Equal(ReadOnlyReason.Malformed, r[1].Reason);
        Assert.Equal(ReadOnlyReason.Malformed, r[2].Reason);
    }

    [Fact]
    public void Read_CloudSquare_EditableWithRd_PlainSquareWithRd_StillReadOnly()
    {
        var r = ReadWith((doc, page) =>
        {
            var cloud = Add(doc, page, "Square", 10, 10, 110, 60);
            var be = new PdfDictionary(doc);
            be.Elements["/S"] = new PdfName("/C");
            be.Elements["/I"] = new PdfReal(1);
            cloud.Elements["/BE"] = be;
            cloud.Elements["/RD"] = Nums(doc, 5, 5, 5, 5);
            var plain = Add(doc, page, "Square", 10, 10, 110, 60);
            plain.Elements["/RD"] = Nums(doc, 5, 5, 5, 5);
            var circleCloud = Add(doc, page, "Circle", 10, 10, 110, 60);
            circleCloud.Elements["/BE"] = be;
            circleCloud.Elements["/RD"] = Nums(doc, 5, 5, 5, 5);
        });
        Assert.Equal(ReadOnlyReason.None, r[0].Reason);
        Assert.Equal(1f, r[0].Data!.CloudIntensity);
        Assert.Equal(new PdfRectF(10, 10, 110, 60), r[0].Data!.Rect);
        Assert.Equal(ReadOnlyReason.RectDifferences, r[1].Reason);
        Assert.Equal(ReadOnlyReason.None, r[2].Reason);   // Circle은 클라우드로 그리지 못하므로 매끈한 타원으로 읽는다
        Assert.Equal(0f, r[2].Data!.CloudIntensity);
    }

    [Fact]
    public void Read_Caret_Editable()
    {
        var r = ReadWith((doc, page) =>
        {
            var d = Add(doc, page, "Caret", 10, 10, 30, 30);
            d.Elements["/Sy"] = new PdfName("/P");
            d.Elements["/C"] = Nums(doc, 1, 0, 0);
            d.Elements["/Contents"] = new PdfString("ins");
        });
        var o = Assert.Single(r);
        Assert.Equal(ReadOnlyReason.None, o.Reason);
        Assert.Equal(AnnotKind.Caret, o.Data!.Kind);
        Assert.Equal("ins", o.Data.Contents);
        Assert.Equal("#FF0000", o.Data.Color!.Value.ToHex());
    }
}
