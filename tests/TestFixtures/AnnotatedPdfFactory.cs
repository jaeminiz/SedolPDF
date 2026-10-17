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
    /// A4 1쪽. /Annots 순서: [0] Link, [1] Square, [2] Highlight, [3] Text, [4] Popup(→3), [5] Text 답글(→3, /RT /Group이라 편집 불가),
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
        // M3b부터 /RT /R 답글은 편집할 수 있다(CreateThreads). 이 픽스처의 [5]는 "편집할 수 없는 원본 답글" 용도라 지원하지 않는 묶음 /RT /Group으로 둔다.
        reply.Elements["/RT"] = new PdfName("/Group");
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

    /// <summary>
    /// 패치 길 유형이 든 원본 주석 모음 (userPassword가 null이면 암호 없음). /Annots 순서: [0] Square, [1] Line, [2] FreeText,
    /// [3] Polygon, [4] Square 클라우드, [5] StrikeOut(NM strike-1), [6] Caret(/IRT→5, /RT /Group), [7] Text, [8] Ink.
    /// </summary>
    public static void CreatePatchKinds(string path, string? userPassword)
    {
        using var doc = NewDoc(out var page);
        var line = Annot(doc, "Line", 100, 500, 200, 540, "0 0 0 RG 100 500 m 200 540 l S");
        line.Elements["/L"] = Nums(doc, 100, 500, 200, 540);
        line.Elements["/C"] = Nums(doc, 0, 0, 0);
        var ft = Annot(doc, "FreeText", 100, 420, 260, 460, "0 g 100 420 160 40 re S");
        ft.Elements["/Contents"] = new PdfString("hello", PdfStringEncoding.Unicode);
        ft.Elements["/DA"] = new PdfString("0 0 0 rg /Helv 12 Tf");
        var poly = Annot(doc, "Polygon", 300, 420, 400, 520, "0 0 1 RG 300 420 m 400 420 l 350 520 l s");
        poly.Elements["/Vertices"] = Nums(doc, 300, 420, 400, 420, 350, 520);
        poly.Elements["/C"] = Nums(doc, 0, 0, 1);
        var cloud = Annot(doc, "Square", 320, 300, 420, 360, "0 0 1 RG 320 300 100 60 re S");
        cloud.Elements["/BE"] = Dict(doc, ("/S", new PdfName("/C")), ("/I", new PdfInteger(1)));
        cloud.Elements["/C"] = Nums(doc, 0, 0, 1);
        var strike = Markup(doc, "StrikeOut", 100, 250, 300, 265, [1, 0, 0]);
        strike.Elements["/NM"] = new PdfString("strike-1");
        var caret = Annot(doc, "Caret", 296, 246, 312, 266, "0 0 1 rg 296 246 16 20 re f");
        caret.Elements["/NM"] = new PdfString("caret-1");
        caret.Elements["/C"] = Nums(doc, 0, 0, 1);
        caret.Elements["/Contents"] = new PdfString("new text", PdfStringEncoding.Unicode);
        caret.Elements["/Sy"] = new PdfName("/None");
        caret.Elements["/IRT"] = strike.Reference!;
        caret.Elements["/RT"] = new PdfName("/Group");
        var text = TextNote(doc);
        var ink = Annot(doc, "Ink", 100, 150, 220, 200, "0 0 0 RG 2 w 100 150 m 220 200 l S");
        ink.Elements["/InkList"] = new PdfArray(doc, Nums(doc, 100, 150, 220, 200));
        ink.Elements["/Border"] = Nums(doc, 0, 0, 2);
        ink.Elements["/C"] = Nums(doc, 0, 0, 0);
        SetAnnots(doc, page, [RedSquare(doc), line, ft, poly, cloud, strike, caret, text, ink]);
        if (userPassword is not null)
        {
            doc.SecuritySettings.UserPassword = userPassword;
            doc.SecuritySettings.OwnerPassword = userPassword + "-owner";
        }
        Save(doc, path);
    }

    /// <summary>
    /// 답글·검토 상태가 든 원본. /Annots 순서: [0] Text 메모(NM note-1), [1] Popup(→0), [2] 답글(NM reply-1, /IRT→0, /RT /R, /XKeep),
    /// [3] 상태 Accepted(NM state-1, /F 30), [4] 상태 Rejected(NM state-2, [3]보다 나중), [5] Link, [6] 답글(/IRT→5: 부모가 읽기 전용이라 읽기 전용),
    /// [7] 상태 Marked 모델(NM state-3: 지원하지 않는 모델이라 읽기 전용), [8] 답글에 단 답글(NM reply-2, /IRT→2).
    /// </summary>
    public static void CreateThreads(string path)
    {
        using var doc = NewDoc(out var page);
        var note = TextNote(doc);
        note.Elements["/NM"] = new PdfString("note-1");
        var popup = PopupFor(doc, note);
        var reply = Annot(doc, "Text", 100, 560, 124, 584, "0 0 1 RG 100 560 24 24 re S");
        reply.Elements["/NM"] = new PdfString("reply-1");
        reply.Elements["/IRT"] = note.Reference!;
        reply.Elements["/RT"] = new PdfName("/R");
        reply.Elements["/Contents"] = new PdfString(ReplyText, PdfStringEncoding.Unicode);
        reply.Elements["/T"] = new PdfString("답변자", PdfStringEncoding.Unicode);
        reply.Elements["/M"] = new PdfString("D:20260115100000+09'00'");
        reply.Elements["/XKeep"] = new PdfString("keep-me");
        var s1 = StateAnnot(doc, note, "state-1", "Accepted", "Review", "D:20260116090000+09'00'");
        var s2 = StateAnnot(doc, note, "state-2", "Rejected", "Review", "D:20260117090000+09'00'");
        var link = Link(doc);
        var onLink = Annot(doc, "Text", 50, 760, 74, 784, "0 0 1 RG 50 760 24 24 re S");
        onLink.Elements["/IRT"] = link.Reference!;
        onLink.Elements["/RT"] = new PdfName("/R");
        var marked = StateAnnot(doc, note, "state-3", "Marked", "Marked", "D:20260118090000+09'00'");
        var nested = Annot(doc, "Text", 100, 560, 124, 584, "0 0 1 RG 100 560 24 24 re S");
        nested.Elements["/NM"] = new PdfString("reply-2");
        nested.Elements["/IRT"] = reply.Reference!;
        nested.Elements["/RT"] = new PdfName("/R");
        nested.Elements["/Contents"] = new PdfString("답답글", PdfStringEncoding.Unicode);
        nested.Elements["/M"] = new PdfString("D:20260115110000+09'00'");
        SetAnnots(doc, page, [note, popup, reply, s1, s2, link, onLink, marked, nested]);
        Save(doc, path);
    }

    private static PdfDictionary StateAnnot(PdfSharp.Pdf.PdfDocument doc, PdfDictionary parent, string nm, string state, string model, string modified)
    {
        var a = Annot(doc, "Text", 100, 560, 124, 584, "");
        a.Elements["/F"] = new PdfInteger(30);
        a.Elements["/NM"] = new PdfString(nm);
        a.Elements["/IRT"] = parent.Reference!;
        a.Elements["/State"] = new PdfString(state);
        a.Elements["/StateModel"] = new PdfString(model);
        a.Elements["/T"] = new PdfString("검토자", PdfStringEncoding.Unicode);
        a.Elements["/M"] = new PdfString(modified);
        a.Elements["/Contents"] = new PdfString("상태 " + state, PdfStringEncoding.Unicode);
        return a;
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
