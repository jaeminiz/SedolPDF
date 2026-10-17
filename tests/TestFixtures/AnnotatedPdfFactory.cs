using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.TestFixtures;

/// <summary>
/// 원본 주석(다른 프로그램이 만든 것처럼)이 들어 있는 테스트 PDF 생성기. PDFsharp 저수준 딕셔너리로 직접 만들고,
/// 각 주석에 간단한 /AP /N 폼(BBox = Rect)을 넣어 PDFium이 그리게 한다.
/// </summary>
public static class AnnotatedPdfFactory
{
    public const string MemoText = "원본 메모";
    public const string ReviewerName = "검토자";
    public const string ReplyText = "답글";
    public const string ModifiedDate = "D:20260115093000+09'00'";

    /// <summary>
    /// A4 1쪽. /Annots 순서: [0] Link, [1] Square, [2] Highlight, [3] Text, [4] Popup(→3), [5] Text 답글(→3),
    /// [6] Square 클라우드, [7] Ink, [8] Underline, [9] StrikeOut, [10] Circle, [11] Square 회색, [12] Square CMYK.
    /// </summary>
    public static void CreateMixed(string path)
    {
        using var doc = NewDoc(out var page);
        var list = new List<PdfDictionary>();

        list.Add(Link(doc));
        list.Add(RedSquare(doc));
        list.Add(YellowHighlight(doc));
        var text = TextNote(doc);
        var popup = PopupFor(doc, text);
        list.Add(text);
        list.Add(popup);

        var reply = Annot(doc, "Text", 120, 560, 144, 584, "0 0 1 RG 120 560 24 24 re S");
        reply.Elements["/IRT"] = text.Reference!;
        reply.Elements["/RT"] = new PdfName("/R");
        reply.Elements["/Contents"] = new PdfString(ReplyText, PdfStringEncoding.Unicode);
        list.Add(reply);

        var cloud = Annot(doc, "Square", 320, 500, 420, 560, "0 0 1 RG 320 500 100 60 re S");
        cloud.Elements["/BE"] = Dict(doc, ("/S", new PdfName("/C")), ("/I", new PdfInteger(1)));
        list.Add(cloud);

        var ink = Annot(doc, "Ink", 100, 400, 220, 480, "0 0 0 RG 2 w 100 400 m 160 480 l S 160 400 m 220 480 l S");
        ink.Elements["/InkList"] = new PdfArray(doc,
            Nums(doc, 100, 400, 160, 480), Nums(doc, 160, 400, 220, 480));
        ink.Elements["/Border"] = Nums(doc, 0, 0, 2);
        ink.Elements["/C"] = Nums(doc, 0, 0, 0);
        list.Add(ink);

        list.Add(Markup(doc, "Underline", 100, 340, 300, 355, [0, 0.5, 0]));
        list.Add(Markup(doc, "StrikeOut", 100, 300, 300, 315, [1, 0, 0]));

        var circle = Annot(doc, "Circle", 320, 400, 420, 460, "0 0.6 0.3 rg 320 400 100 60 re f");
        circle.Elements["/IC"] = Nums(doc, 0, 0.6, 0.3);
        circle.Elements["/C"] = Nums(doc, 0, 0, 1);
        list.Add(circle);

        var gray = Annot(doc, "Square", 100, 200, 200, 260, "0.5 G 100 200 100 60 re S");
        gray.Elements["/C"] = Nums(doc, 0.5);
        list.Add(gray);

        var cmyk = Annot(doc, "Square", 220, 200, 320, 260, "1 0 0 RG 220 200 100 60 re S");
        cmyk.Elements["/C"] = Nums(doc, 0, 1, 1, 0);
        list.Add(cmyk);

        SetAnnots(doc, page, list);
        Save(doc, path);
    }

    /// <summary>
    /// 텍스트 바꾸기 묶음(Acrobat 방식). /Annots 순서: [0] StrikeOut(NM strike-1), [1] Caret(/IRT→0, /RT /Group, NM caret-1),
    /// [2] Text 메모(NM note-1), [3] Caret(/IRT→2, /RT /Group: 부모가 StrikeOut이 아니므로 읽기 전용 답글로 남아야 함).
    /// </summary>
    public static void CreateReplaceGroup(string path)
    {
        using var doc = NewDoc(out var page);
        var strike = Markup(doc, "StrikeOut", 100, 300, 300, 315, [1, 0, 0]);
        strike.Elements["/NM"] = new PdfString("strike-1");
        var caret = Annot(doc, "Caret", 296, 296, 312, 316, "0 0 1 rg 296 296 16 20 re f");
        caret.Elements["/NM"] = new PdfString("caret-1");
        caret.Elements["/C"] = Nums(doc, 0, 0, 1);
        caret.Elements["/Contents"] = new PdfString("new text", PdfStringEncoding.Unicode);
        caret.Elements["/Sy"] = new PdfName("/None");
        caret.Elements["/IRT"] = strike.Reference!;
        caret.Elements["/RT"] = new PdfName("/Group");
        var note = TextNote(doc);
        note.Elements["/NM"] = new PdfString("note-1");
        var odd = Annot(doc, "Caret", 396, 396, 412, 416, "0 0 1 rg 396 396 16 20 re f");
        odd.Elements["/NM"] = new PdfString("caret-2");
        odd.Elements["/IRT"] = note.Reference!;
        odd.Elements["/RT"] = new PdfName("/Group");
        SetAnnots(doc, page, [strike, caret, note, odd]);
        Save(doc, path);
    }

    /// <summary>[1] Square, [3] Text, [4] Popup만 있고 사용자(열기) 암호가 걸린 문서.</summary>
    public static void CreateEncryptedWithAnnotations(string path, string userPassword)
    {
        using var doc = NewDoc(out var page);
        var text = TextNote(doc);
        SetAnnots(doc, page, [RedSquare(doc), text, PopupFor(doc, text)]);
        doc.SecuritySettings.UserPassword = userPassword;
        doc.SecuritySettings.OwnerPassword = userPassword + "-owner";
        Save(doc, path);
    }

    /// <summary>사용자 암호 없음, 소유자 암호 있음. 주석 권한만 allowAnnotations로 정한다. 주석은 Square 1개.</summary>
    public static void CreateOwnerRestrictedWithAnnotations(string path, bool allowAnnotations)
    {
        using var doc = NewDoc(out var page);
        SetAnnots(doc, page, [RedSquare(doc)]);
        var s = doc.SecuritySettings;
        s.OwnerPassword = "owner-secret";
        s.PermitAnnotations = allowAnnotations;
        Save(doc, path);
    }

    // ---- 주석 조각 ----

    private static PdfDictionary Link(PdfSharp.Pdf.PdfDocument doc)
        => Annot(doc, "Link", 50, 760, 200, 780, null);

    private static PdfDictionary RedSquare(PdfSharp.Pdf.PdfDocument doc)
    {
        var a = Annot(doc, "Square", 100, 600, 300, 650, "1 0 0 RG 3 w 101.5 601.5 197 47 re S");
        a.Elements["/C"] = Nums(doc, 1, 0, 0);
        a.Elements["/BS"] = Dict(doc, ("/W", new PdfInteger(3)));
        a.Elements["/CA"] = new PdfReal(0.8);
        a.Elements["/XKeep"] = new PdfString("abc");
        return a;
    }

    private static PdfDictionary YellowHighlight(PdfSharp.Pdf.PdfDocument doc)
    {
        var a = Annot(doc, "Highlight", 100, 680, 300, 715, "1 1 0 rg 100 700 200 15 re f 100 680 150 15 re f");
        a.Elements["/C"] = Nums(doc, 1, 1, 0);
        a.Elements["/CA"] = new PdfReal(0.5);
        a.Elements["/QuadPoints"] = Nums(doc,
            100, 715, 300, 715, 100, 700, 300, 700,
            100, 695, 250, 695, 100, 680, 250, 680);
        return a;
    }

    private static PdfDictionary TextNote(PdfSharp.Pdf.PdfDocument doc)
    {
        var a = Annot(doc, "Text", 100, 560, 124, 584, "1 1 0 rg 100 560 24 24 re f");
        a.Elements["/Contents"] = new PdfString(MemoText, PdfStringEncoding.Unicode);
        a.Elements["/T"] = new PdfString(ReviewerName, PdfStringEncoding.Unicode);
        a.Elements["/Name"] = new PdfName("/Comment");
        a.Elements["/RC"] = new PdfString("<body xmlns=\"http://www.w3.org/1999/xhtml\"><p>memo</p></body>");
        a.Elements["/M"] = new PdfString(ModifiedDate);
        return a;
    }

    private static PdfDictionary PopupFor(PdfSharp.Pdf.PdfDocument doc, PdfDictionary parent)
    {
        var p = Annot(doc, "Popup", 400, 560, 560, 640, null);
        p.Elements["/Parent"] = parent.Reference!;
        p.Elements["/Open"] = new PdfBoolean(false);
        parent.Elements["/Popup"] = p.Reference!;
        return p;
    }

    private static PdfDictionary Markup(PdfSharp.Pdf.PdfDocument doc, string subtype, double l, double b, double r, double t, double[] c)
    {
        var a = Annot(doc, subtype, l, b, r, t, $"{c[0]:0.##} {c[1]:0.##} {c[2]:0.##} RG 1 w {l} {(b + t) / 2} m {r} {(b + t) / 2} l S");
        a.Elements["/C"] = Nums(doc, c);
        a.Elements["/QuadPoints"] = Nums(doc, l, t, r, t, l, b, r, b);
        return a;
    }

    // ---- 저수준 도우미 ----

    private static PdfSharp.Pdf.PdfDocument NewDoc(out PdfPage page)
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        page = doc.AddPage();
        page.Width = new XUnitPt(595);
        page.Height = new XUnitPt(842);
        return doc;
    }

    private static PdfDictionary Annot(PdfSharp.Pdf.PdfDocument doc, string subtype, double l, double b, double r, double t, string? apContent)
    {
        var a = new PdfDictionary(doc);
        a.Elements["/Type"] = new PdfName("/Annot");
        a.Elements["/Subtype"] = new PdfName("/" + subtype);
        a.Elements["/Rect"] = Nums(doc, l, b, r, t);
        if (subtype != "Popup") a.Elements["/F"] = new PdfInteger(4);
        if (apContent is not null)
        {
            var form = new PdfDictionary(doc);
            form.Elements["/Type"] = new PdfName("/XObject");
            form.Elements["/Subtype"] = new PdfName("/Form");
            form.Elements["/BBox"] = Nums(doc, l, b, r, t);
            form.CreateStream(Encoding.ASCII.GetBytes(apContent));
            doc.Internals.AddObject(form);
            a.Elements["/AP"] = Dict(doc, ("/N", form.Reference!));
        }
        doc.Internals.AddObject(a);
        return a;
    }

    private static PdfArray Nums(PdfSharp.Pdf.PdfDocument doc, params double[] values)
    {
        var arr = new PdfArray(doc);
        foreach (var v in values) arr.Elements.Add(new PdfReal(v));
        return arr;
    }

    private static PdfDictionary Dict(PdfSharp.Pdf.PdfDocument doc, params (string Key, PdfItem Value)[] items)
    {
        var d = new PdfDictionary(doc);
        foreach (var (k, v) in items) d.Elements[k] = v;
        return d;
    }

    private static void SetAnnots(PdfSharp.Pdf.PdfDocument doc, PdfPage page, IEnumerable<PdfDictionary> annots)
    {
        var arr = new PdfArray(doc);
        foreach (var a in annots) arr.Elements.Add(a.Reference!);
        page.Elements["/Annots"] = arr;
    }

    private static void Save(PdfSharp.Pdf.PdfDocument doc, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        doc.Save(path);
    }
}
