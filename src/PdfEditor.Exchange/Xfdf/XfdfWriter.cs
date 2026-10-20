using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Exchange.Xfdf;

/// <summary>
/// 주석을 XFDF(Adobe XML 주석 교환 형식)로 내보낸다 (M3b B8).
/// 쪽 번호는 0부터, 좌표는 PDF 사용자 좌표, 숫자는 InvariantCulture 소수 셋째 자리까지.
/// 읽기 전용 원본은 리더가 읽은 값(종류·위치·작성자·내용·시각)만 내보내며 AP는 내보내지 않는다.
/// </summary>
public static class XfdfWriter
{
    public static readonly XNamespace Ns = "http://ns.adobe.com/xfdf/";
    private static readonly XName Space = XNamespace.Xml + "space";

    public static XDocument Write(
        IReadOnlyList<(int PageIndex, Annotation A)> annotations,
        string pdfFileName,
        AnnotationAssets assets,
        IReadOnlyList<(int PageIndex, ReadOnlyAnnotationInfo Info)>? readOnly = null)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(assets);

        // 답글이 부모를 가리킬 /NM: 이름이 없는 주석에는 안정적인 이름을 만들어 준다.
        var names = new Dictionary<AnnotId, string>();
        foreach (var (_, a) in annotations)
            names[a.Id] = string.IsNullOrEmpty(a.Name) ? "sedol-" + a.Id.Value.ToString(CultureInfo.InvariantCulture) : a.Name;

        var annots = new XElement(Ns + "annots");
        foreach (var (page, a) in annotations)
            annots.Add(WriteOne(page, a, names, assets));
        if (readOnly is not null)
            foreach (var (page, info) in readOnly)
                if (WriteReadOnly(page, info) is { } e) annots.Add(e);

        return new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(Ns + "xfdf",
                new XAttribute(Space, "preserve"),
                new XElement(Ns + "f", new XAttribute("href", Clean(pdfFileName ?? ""))),
                annots));
    }

    private static XElement WriteOne(int page, Annotation a, Dictionary<AnnotId, string> names, AnnotationAssets assets)
    {
        var e = new XElement(Ns + ElementName(a.Kind));
        e.SetAttributeValue("page", page.ToString(CultureInfo.InvariantCulture));
        e.SetAttributeValue("rect", Rect(a.Rect));
        if (a.Kind == AnnotKind.FreeText)
        {
            // freetext의 color(= /C)는 배경이다. 테두리 색은 defaultappearance의 RG로 쓴다(FreeText 아래).
            if (a.FillColor is { } bg) e.SetAttributeValue("color", bg.ToHex());
        }
        else
        {
            if (a.Color is { } c) e.SetAttributeValue("color", c.ToHex());
            if (a.FillColor is { } fc) e.SetAttributeValue("interior-color", fc.ToHex());
        }
        if (a.Kind is AnnotKind.FreeText or AnnotKind.Stamp && AnnotationRotation.Quarter(a.Rotation) != 0)
            e.SetAttributeValue("rotation", (AnnotationRotation.Quarter(a.Rotation) * 90).ToString(CultureInfo.InvariantCulture));
        e.SetAttributeValue("opacity", Num(a.Opacity));
        e.SetAttributeValue("width", Num(a.BorderWidth));
        e.SetAttributeValue("name", Clean(names[a.Id]));
        if (!string.IsNullOrEmpty(a.Author)) e.SetAttributeValue("title", Clean(a.Author));
        if (!string.IsNullOrEmpty(a.Subject)) e.SetAttributeValue("subject", Clean(a.Subject));
        if (a.ModifiedAt is { } m) e.SetAttributeValue("date", PdfDate.Format(m));
        if (a.CreatedAt is { } cr) e.SetAttributeValue("creationdate", PdfDate.Format(cr));
        if (Flags(a.Flags) is { Length: > 0 } fl) e.SetAttributeValue("flags", fl);

        if (a.InReplyTo is { } parent && names.TryGetValue(parent, out var pn))
        {
            e.SetAttributeValue("inreplyto", Clean(pn));
            if (a.ReplyKind == ReplyType.Group) e.SetAttributeValue("replyType", "group");
        }
        if (a.State is { } st)
        {
            e.SetAttributeValue("statemodel", "Review");
            e.SetAttributeValue("state", st.ToPdfName());
        }

        if (a.CloudIntensity > 0 && a.Kind is AnnotKind.Square or AnnotKind.Circle or AnnotKind.Polygon)
        {
            e.SetAttributeValue("style", "cloudy");
            e.SetAttributeValue("intensity", Num(a.CloudIntensity));
        }

        switch (a.Kind)
        {
            case AnnotKind.Highlight or AnnotKind.Underline or AnnotKind.StrikeOut:
                if (!a.Quads.IsDefaultOrEmpty)
                    e.SetAttributeValue("coords", string.Join(",", a.Quads.Select(Quad)));
                break;
            case AnnotKind.Ink:
                if (!a.InkStrokes.IsDefaultOrEmpty)
                    e.Add(new XElement(Ns + "inklist", a.InkStrokes.Select(s => new XElement(Ns + "gesture", Points(s)))));
                break;
            case AnnotKind.Line when a.Vertices.Length >= 2:
                e.SetAttributeValue("start", Pt(a.Vertices[0]));
                e.SetAttributeValue("end", Pt(a.Vertices[1]));
                Endings(e, a);
                break;
            case AnnotKind.PolyLine or AnnotKind.Polygon:
                if (!a.Vertices.IsDefaultOrEmpty) e.Add(new XElement(Ns + "vertices", Points(a.Vertices)));
                if (a.Kind == AnnotKind.PolyLine) Endings(e, a);
                break;
            case AnnotKind.FreeText:
                FreeText(e, a);
                break;
            case AnnotKind.Stamp:
                if (a.Stamp is { Kind: StampKind.Builtin } sp && !string.IsNullOrEmpty(sp.BuiltinName))
                    e.SetAttributeValue("icon", Clean(sp.BuiltinName));
                break;
            case AnnotKind.FileAttachment:
                Attachment(e, a, assets);
                break;
        }

        string? contents = a.Contents;
        if (string.IsNullOrEmpty(contents) && a is { Kind: AnnotKind.Stamp, Stamp: { Kind: StampKind.Text } ts }) contents = ts.Text;
        if (!string.IsNullOrEmpty(contents)) e.Add(new XElement(Ns + "contents", Clean(contents)));
        return e;
    }

    private static void FreeText(XElement e, Annotation a)
    {
        var tc = a.TextColor ?? new PdfColor(0, 0, 0);
        string da = string.Create(CultureInfo.InvariantCulture,
            $"/Helv {Num(a.FontSizePt)} Tf {tc.R / 255.0:0.###} {tc.G / 255.0:0.###} {tc.B / 255.0:0.###} rg");
        if (a.Color is { } bc)
            da += string.Create(CultureInfo.InvariantCulture, $" {bc.R / 255.0:0.###} {bc.G / 255.0:0.###} {bc.B / 255.0:0.###} RG");
        e.Add(new XElement(Ns + "defaultappearance", da));
        e.SetAttributeValue("justification", a.Align switch { TextAlign.Center => "centered", TextAlign.Right => "right", _ => "left" });
        switch (a.Intent)
        {
            case FreeTextIntent.Typewriter: e.SetAttributeValue("intent", "FreeTextTypewriter"); break;
            case FreeTextIntent.Callout: e.SetAttributeValue("intent", "FreeTextCallout"); break;
        }
        if (!a.Vertices.IsDefaultOrEmpty)
            e.SetAttributeValue("callout", string.Join(",", a.Vertices.Select(Pt)));
        if (a.TextBox is { } tb)
            e.SetAttributeValue("fringe", string.Join(",", [Num(tb.Left - a.Rect.Left), Num(tb.Bottom - a.Rect.Bottom), Num(a.Rect.Right - tb.Right), Num(a.Rect.Top - tb.Top)]));
        if (a.Intent == FreeTextIntent.Callout) Endings(e, a);
    }

    private static void Attachment(XElement e, Annotation a, AnnotationAssets assets)
    {
        if (!string.IsNullOrEmpty(a.AttachmentName)) e.SetAttributeValue("file", Clean(a.AttachmentName));
        if (a.AttachmentId is { } id && assets.HasFile(id))
        {
            var (name, data) = assets.GetFile(id);
            if (string.IsNullOrEmpty(a.AttachmentName)) e.SetAttributeValue("file", Clean(name));
            e.Add(new XElement(Ns + "data",
                new XAttribute("MODE", "raw"),
                new XAttribute("encoding", "base64"),
                new XAttribute("length", data.Length.ToString(CultureInfo.InvariantCulture)),
                Convert.ToBase64String(data)));
        }
    }

    private static void Endings(XElement e, Annotation a)
    {
        if (a.StartEnding != LineEnding.None) e.SetAttributeValue("head", a.StartEnding.ToPdfName());
        if (a.EndEnding != LineEnding.None) e.SetAttributeValue("tail", a.EndEnding.ToPdfName());
    }

    private static XElement? WriteReadOnly(int page, ReadOnlyAnnotationInfo info)
    {
        string? name = info.Subtype switch
        {
            PdfAnnotSubtype.Text => "text",
            PdfAnnotSubtype.FreeText => "freetext",
            PdfAnnotSubtype.Line => "line",
            PdfAnnotSubtype.Square => "square",
            PdfAnnotSubtype.Circle => "circle",
            PdfAnnotSubtype.Polygon => "polygon",
            PdfAnnotSubtype.PolyLine => "polyline",
            PdfAnnotSubtype.Highlight => "highlight",
            PdfAnnotSubtype.Underline => "underline",
            PdfAnnotSubtype.Squiggly => "squiggly",
            PdfAnnotSubtype.StrikeOut => "strikeout",
            PdfAnnotSubtype.Stamp => "stamp",
            PdfAnnotSubtype.Caret => "caret",
            PdfAnnotSubtype.Ink => "ink",
            PdfAnnotSubtype.FileAttachment => "fileattachment",
            _ => null,   // 링크·위젯·Popup·모르는 유형은 내보내지 않는다
        };
        if (name is null) return null;
        var e = new XElement(Ns + name);
        e.SetAttributeValue("page", page.ToString(CultureInfo.InvariantCulture));
        e.SetAttributeValue("rect", Rect(info.Rect));
        if (!string.IsNullOrEmpty(info.Author)) e.SetAttributeValue("title", Clean(info.Author));
        if (info.ModifiedAt is { } m) e.SetAttributeValue("date", PdfDate.Format(m));
        if (!string.IsNullOrEmpty(info.Contents)) e.Add(new XElement(Ns + "contents", Clean(info.Contents)));
        return e;
    }

    private static string ElementName(AnnotKind k) => k switch
    {
        AnnotKind.Ink => "ink",
        AnnotKind.Highlight => "highlight",
        AnnotKind.Underline => "underline",
        AnnotKind.StrikeOut => "strikeout",
        AnnotKind.Text => "text",
        AnnotKind.Square => "square",
        AnnotKind.Circle => "circle",
        AnnotKind.Line => "line",
        AnnotKind.PolyLine => "polyline",
        AnnotKind.Polygon => "polygon",
        AnnotKind.FreeText => "freetext",
        AnnotKind.Caret => "caret",
        AnnotKind.Stamp => "stamp",
        AnnotKind.FileAttachment => "fileattachment",
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    private static string Flags(int f)
    {
        var l = new List<string>(4);
        if ((f & PdfAnnotFlags.Invisible) != 0) l.Add("invisible");
        if ((f & PdfAnnotFlags.Hidden) != 0) l.Add("hidden");
        if ((f & PdfAnnotFlags.Print) != 0) l.Add("print");
        if ((f & PdfAnnotFlags.NoZoom) != 0) l.Add("nozoom");
        if ((f & PdfAnnotFlags.NoRotate) != 0) l.Add("norotate");
        if ((f & 32) != 0) l.Add("noview");
        if ((f & 64) != 0) l.Add("readonly");
        if ((f & 128) != 0) l.Add("locked");
        if ((f & 512) != 0) l.Add("lockedcontents");
        return string.Join(",", l);
    }

    // ---- 숫자·좌표 ----
    private static string Num(float v)
    {
        double r = Math.Round(v, 3, MidpointRounding.AwayFromZero);
        if (r == 0) r = 0;   // -0 방지
        return r.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string Pt(PdfPointF p) => Num(p.X) + "," + Num(p.Y);
    private static string Points(IEnumerable<PdfPointF> pts) => string.Join(";", pts.Select(Pt));
    private static string Rect(PdfRectF r) => string.Join(",", [Num(r.Left), Num(r.Bottom), Num(r.Right), Num(r.Top)]);
    private static string Quad(PdfQuadF q) => string.Join(",", [Pt(q.P1), Pt(q.P2), Pt(q.P3), Pt(q.P4)]);

    /// <summary>XML 1.0에서 쓸 수 없는 글자(제어 문자, 짝 없는 서로게이트, U+FFFE/FFFF)를 뺀다.</summary>
    internal static string Clean(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            bool ok;
            int skip = 0;
            if (char.IsHighSurrogate(ch))
            {
                ok = i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
                if (ok) skip = 1;
            }
            else ok = ch is '\t' or '\n' or '\r' || (ch >= 0x20 && !char.IsLowSurrogate(ch) && ch != '￾' && ch != '￿');
            if (ok)
            {
                sb?.Append(ch);
                if (skip == 1) { i++; sb?.Append(s[i]); }
            }
            else
            {
                sb ??= new StringBuilder(s, 0, i, s.Length);
            }
        }
        return sb?.ToString() ?? s;
    }
}
