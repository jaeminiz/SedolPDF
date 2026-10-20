using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Exchange.Xfdf;

namespace PdfEditor.Exchange.Tests;

public class XfdfWriterTests
{
    private static readonly XNamespace Ns = "http://ns.adobe.com/xfdf/";
    private static readonly PdfRectF R = new(10f, 20f, 110.5f, 70f);

    private static Annotation A(long id, AnnotKind k, Func<Annotation, Annotation>? f = null)
    {
        var a = new Annotation { Id = new AnnotId(id), Kind = k, Rect = R, Color = new PdfColor(255, 0, 0), Name = "nm" + id, Author = "Kim", Subject = "Sub" };
        return f is null ? a : f(a);
    }

    private static XElement One(Annotation a, AnnotationAssets? assets = null)
    {
        var doc = XfdfWriter.Write([(2, a)], "x.pdf", assets ?? AnnotationAssets.Empty);
        return doc.Root!.Element(Ns + "annots")!.Elements().Single();
    }

    [Fact]
    public void Write_SchemaShape_RootNamespaceAndF()
    {
        var doc = XfdfWriter.Write([], "my file.pdf", AnnotationAssets.Empty);
        Assert.Equal(Ns + "xfdf", doc.Root!.Name);
        Assert.Equal("preserve", (string?)doc.Root.Attribute(XNamespace.Xml + "space"));
        Assert.Equal("my file.pdf", (string?)doc.Root.Element(Ns + "f")!.Attribute("href"));
        Assert.Empty(doc.Root.Element(Ns + "annots")!.Elements());
        XDocument.Parse(doc.ToString());
    }

    [Fact]
    public void Write_AllKinds_ExpectedElementsAndAttributes()
    {
        var quad = ImmutableArray.Create(PdfQuadF.FromRect(R));
        var pts = ImmutableArray.Create(new PdfPointF(1, 2), new PdfPointF(3.5f, 4));
        var assets = new AnnotationAssets();
        string fid = assets.AddFile("a.txt", [1, 2, 3]);

        var cases = new (AnnotKind K, string El, Func<Annotation, Annotation> F)[]
        {
            (AnnotKind.Ink, "ink", a => a with { InkStrokes = [pts, pts] }),
            (AnnotKind.Highlight, "highlight", a => a with { Quads = quad }),
            (AnnotKind.Underline, "underline", a => a with { Quads = quad }),
            (AnnotKind.StrikeOut, "strikeout", a => a with { Quads = quad }),
            (AnnotKind.Text, "text", a => a),
            (AnnotKind.Square, "square", a => a with { FillColor = new PdfColor(0, 255, 0), CloudIntensity = 2 }),
            (AnnotKind.Circle, "circle", a => a),
            (AnnotKind.Line, "line", a => a with { Vertices = pts, StartEnding = LineEnding.OpenArrow, EndEnding = LineEnding.Circle }),
            (AnnotKind.PolyLine, "polyline", a => a with { Vertices = pts }),
            (AnnotKind.Polygon, "polygon", a => a with { Vertices = pts }),
            (AnnotKind.FreeText, "freetext", a => a with { Contents = "hi", TextColor = new PdfColor(0, 0, 255), FontSizePt = 14, Intent = FreeTextIntent.Callout, Vertices = pts, TextBox = new PdfRectF(12, 22, 100, 60) }),
            (AnnotKind.Caret, "caret", a => a),
            (AnnotKind.Stamp, "stamp", a => a with { Stamp = new StampSpec(StampKind.Builtin, "Approved", null, null) }),
            (AnnotKind.FileAttachment, "fileattachment", a => a with { AttachmentId = fid, AttachmentName = "a.txt" }),
        };
        foreach (var (k, el, f) in cases)
        {
            var e = One(A(1, k, f), assets);
            Assert.Equal(Ns + el, e.Name);
            Assert.Equal("2", (string?)e.Attribute("page"));
            Assert.Equal("10,20,110.5,70", (string?)e.Attribute("rect"));
            // freetext의 color는 배경색(/C), 테두리 색(#FF0000)은 defaultappearance의 RG
            Assert.Equal(k == AnnotKind.FreeText ? null : "#FF0000", (string?)e.Attribute("color"));
            Assert.Equal("nm1", (string?)e.Attribute("name"));
            Assert.Equal("Kim", (string?)e.Attribute("title"));
            Assert.Equal("Sub", (string?)e.Attribute("subject"));
        }

        var ink = One(cases[0].F(A(1, AnnotKind.Ink)));
        Assert.Equal(["1,2;3.5,4", "1,2;3.5,4"], ink.Element(Ns + "inklist")!.Elements(Ns + "gesture").Select(g => g.Value));
        var hl = One(cases[1].F(A(1, AnnotKind.Highlight)));
        Assert.Equal("10,70,110.5,70,10,20,110.5,20", (string?)hl.Attribute("coords"));
        var sq = One(cases[5].F(A(1, AnnotKind.Square)));
        Assert.Equal("#00FF00", (string?)sq.Attribute("interior-color"));
        Assert.Equal("cloudy", (string?)sq.Attribute("style"));
        Assert.Equal("2", (string?)sq.Attribute("intensity"));
        var ln = One(cases[7].F(A(1, AnnotKind.Line)));
        Assert.Equal("1,2", (string?)ln.Attribute("start"));
        Assert.Equal("3.5,4", (string?)ln.Attribute("end"));
        Assert.Equal("OpenArrow", (string?)ln.Attribute("head"));
        Assert.Equal("Circle", (string?)ln.Attribute("tail"));
        var pl = One(cases[9].F(A(1, AnnotKind.Polygon)));
        Assert.Equal("1,2;3.5,4", pl.Element(Ns + "vertices")!.Value);
        var ft = One(cases[10].F(A(1, AnnotKind.FreeText)));
        Assert.Equal("hi", ft.Element(Ns + "contents")!.Value);
        Assert.Equal("1,2,3.5,4", (string?)ft.Attribute("callout"));
        Assert.Equal("FreeTextCallout", (string?)ft.Attribute("intent"));
        Assert.Equal("2,2,10.5,10", (string?)ft.Attribute("fringe"));
        Assert.Contains("14 Tf", ft.Element(Ns + "defaultappearance")!.Value);
        Assert.Contains("1 0 0 RG", ft.Element(Ns + "defaultappearance")!.Value);
        var st = One(cases[12].F(A(1, AnnotKind.Stamp)));
        Assert.Equal("Approved", (string?)st.Attribute("icon"));
    }

    [Fact]
    public void Write_RepliesAndStates_InReplyToAndState()
    {
        var parent = A(1, AnnotKind.Text, a => a with { Contents = "p" });
        var reply = A(2, AnnotKind.Text, a => a with { InReplyTo = new AnnotId(1), Contents = "r", ReplyKind = ReplyType.Reply });
        var state = A(3, AnnotKind.Text, a => a with { InReplyTo = new AnnotId(1), State = ReviewState.Accepted, ReplyKind = ReplyType.Reply });
        var grp = A(4, AnnotKind.Caret, a => a with { InReplyTo = new AnnotId(1), ReplyKind = ReplyType.Group, Name = null });
        var doc = XfdfWriter.Write([(0, parent), (0, reply), (0, state), (0, grp)], "x.pdf", AnnotationAssets.Empty);
        var els = doc.Root!.Element(Ns + "annots")!.Elements().ToList();
        Assert.Null(els[0].Attribute("inreplyto"));
        Assert.Equal("nm1", (string?)els[1].Attribute("inreplyto"));
        Assert.Null(els[1].Attribute("replyType"));
        Assert.Equal("Accepted", (string?)els[2].Attribute("state"));
        Assert.Equal("Review", (string?)els[2].Attribute("statemodel"));
        Assert.Equal("group", (string?)els[3].Attribute("replyType"));
        Assert.Equal("nm1", (string?)els[3].Attribute("inreplyto"));
        Assert.False(string.IsNullOrEmpty((string?)els[3].Attribute("name")));
    }

    [Fact]
    public void Write_InvariantCulture_UnderGerman()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var a = A(1, AnnotKind.Square, x => x with { Opacity = 0.5f, BorderWidth = 1.2345f, Rect = new PdfRectF(1.5f, 2.25f, 3.125f, 4.0004f) });
            var e = One(a);
            Assert.Equal("1.5,2.25,3.125,4", (string?)e.Attribute("rect"));
            Assert.Equal("0.5", (string?)e.Attribute("opacity"));
            Assert.Equal("1.235", (string?)e.Attribute("width"));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Write_AttachmentBase64()
    {
        var assets = new AnnotationAssets();
        var data = new byte[] { 0, 1, 2, 250, 251 };
        string id = assets.AddFile("f.bin", data);
        var e = One(A(1, AnnotKind.FileAttachment, a => a with { AttachmentId = id, AttachmentName = "f.bin" }), assets);
        Assert.Equal("f.bin", (string?)e.Attribute("file"));
        var d = e.Element(Ns + "data")!;
        Assert.Equal("base64", (string?)d.Attribute("encoding"));
        Assert.Equal("5", (string?)d.Attribute("length"));
        Assert.Equal(data, Convert.FromBase64String(d.Value));
    }

    [Fact]
    public void Write_ControlCharsInContent_StillWellFormed()
    {
        var a = A(1, AnnotKind.Text, x => x with { Contents = "a\u0001b\u0000c\uD800d\tline\nnext", Author = "x\u0002y", Name = "n\u0003m" });
        var doc = XfdfWriter.Write([(0, a)], "bad\u0001.pdf", AnnotationAssets.Empty);
        var back = XDocument.Parse(doc.ToString());
        var e = back.Root!.Element(Ns + "annots")!.Elements().Single();
        Assert.Equal("abcd\tline\nnext", e.Element(Ns + "contents")!.Value);
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms)) doc.Save(w);
        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void Write_ReadOnlyOriginals_ExportOnlyReaderValues()
    {
        var info = new ReadOnlyAnnotationInfo(0, PdfAnnotSubtype.Stamp, R, "Lee", "note", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), ReadOnlyReason.UnsupportedType);
        var link = info with { Subtype = PdfAnnotSubtype.Link };
        var doc = XfdfWriter.Write([], "x.pdf", AnnotationAssets.Empty, [(1, info), (1, link)]);
        var e = doc.Root!.Element(Ns + "annots")!.Elements().Single();
        Assert.Equal(Ns + "stamp", e.Name);
        Assert.Equal("1", (string?)e.Attribute("page"));
        Assert.Equal("Lee", (string?)e.Attribute("title"));
        Assert.Equal("note", e.Element(Ns + "contents")!.Value);
        Assert.StartsWith("D:20260102030405", (string?)e.Attribute("date"));
        Assert.Null(e.Element(Ns + "data"));
    }

    [Fact]
    public void Write_EmptyList_IsValid()
    {
        var doc = XfdfWriter.Write([], "a.pdf", AnnotationAssets.Empty, []);
        Assert.Empty(doc.Root!.Element(Ns + "annots")!.Elements());
    }
}
