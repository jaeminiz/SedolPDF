using System.Text;
using PdfEditor.Document.Annotations.Patching;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.Appearance.Fonts;

/// <summary>
/// Noto Sans KR 서브셋을 PDF에 넣는다(S1 방식): HarfBuzz 서브셋 → 순수 CFF 테이블 → /FontFile3 /CIDFontType0C,
/// /CIDFontType0 + /Type0 /Identity-H, 글자 코드는 CFF charset의 CID, /W 폭, /ToUnicode.
/// 화면은 전체 글꼴로 글자를 배치하고 PDF는 그 CID를 그대로 쓰므로, 서브셋 뒤에도 CID가 전체 글꼴과 같아야 한다(V11).
/// 다르면 예외를 던진다.
/// </summary>
public static class FontEmbedder
{
    /// <summary>글꼴 객체 묶음을 만들어 /Type0 글꼴의 참조를 돌려준다.</summary>
    public static PdfReference Embed(PdfSharp.Pdf.PdfDocument doc, AppFont font, FontUse use)
    {
        var cps = use.Codepoints.Distinct().Where(c => c is not ('\n' or '\r')).Append(' ').Distinct().ToArray();
        // TrueType(0.12.0 시스템 글꼴): 글리프 번호를 유지한 서브셋 → /CIDFontType2 + /FontFile2 + /CIDToGIDMap /Identity, 글자 코드 = 글리프 번호.
        // CFF(번들 Noto 등): CID 키 방식 서브셋 → /CIDFontType0 + /FontFile3 /CIDFontType0C, 글자 코드 = CID.
        bool trueType = font.Outline == FontOutline.TrueType;
        byte[] sub = FontSubsetter.Subset(font.Data, cps, font.FaceIndex, retainGids: trueType);
        if (trueType) VerifyGids(font, use, sub);
        else VerifyCids(font, use, cps, sub, CffCharset.GetCidByGid(sub));

        double asc = font.Ascender1000, desc = font.Descender1000;
        bool serif = use.Family == FontUse.SerifFamily;
        string psName = use.Family.StartsWith("sys:", StringComparison.Ordinal) && SafeName(font.PostScriptName) is { } ps
            ? ps : serif ? "NotoSerifKR-Regular" : use.Family.Length == 0 ? "NotoSansKR-Regular" : "SedolFont";
        string baseFont = $"{SubsetTag(cps)}+" + psName;

        var fontFile = new PdfDictionary(doc);
        if (trueType)
        {
            fontFile.CreateStream(sub);
            fontFile.Elements.SetInteger("/Length1", sub.Length);
        }
        else
        {
            fontFile.CreateStream(CffCharset.ExtractCff(sub));
            fontFile.Elements.SetName("/Subtype", "/CIDFontType0C");
        }
        doc.Internals.AddObject(fontFile);

        var descriptor = new PdfDictionary(doc);
        descriptor.Elements.SetName("/Type", "/FontDescriptor");
        descriptor.Elements.SetName("/FontName", "/" + baseFont);
        descriptor.Elements.SetInteger("/Flags", serif ? 6 : 4);
        descriptor.Elements["/FontBBox"] = Arr(doc, 0, desc, 1000, asc);
        descriptor.Elements.SetInteger("/ItalicAngle", 0);
        descriptor.Elements.SetInteger("/Ascent", (int)asc);
        descriptor.Elements.SetInteger("/Descent", (int)desc);
        descriptor.Elements.SetInteger("/CapHeight", (int)(asc * 0.8));
        descriptor.Elements.SetInteger("/StemV", 80);
        descriptor.Elements[trueType ? "/FontFile2" : "/FontFile3"] = fontFile.Reference ?? throw new InvalidOperationException();
        doc.Internals.AddObject(descriptor);

        var widths = new SortedDictionary<int, int>();
        var toUnicode = new SortedDictionary<int, string>();
        foreach (var g in use.Glyphs)
        {
            widths[g.Cid] = g.Advance1000;
            if (g.Text.Length > 0 && !toUnicode.ContainsKey(g.Cid)) toUnicode[g.Cid] = g.Text;
        }
        var wArr = new PdfArray(doc);
        foreach (var (cid, w) in widths)
        {
            wArr.Elements.Add(new PdfInteger(cid));
            wArr.Elements.Add(Arr(doc, w));
        }

        var cidFont = new PdfDictionary(doc);
        cidFont.Elements.SetName("/Type", "/Font");
        cidFont.Elements.SetName("/Subtype", trueType ? "/CIDFontType2" : "/CIDFontType0");
        if (trueType) cidFont.Elements.SetName("/CIDToGIDMap", "/Identity");
        cidFont.Elements.SetName("/BaseFont", "/" + baseFont);
        var sysInfo = new PdfDictionary(doc);
        sysInfo.Elements["/Registry"] = new PdfString("Adobe");
        sysInfo.Elements["/Ordering"] = new PdfString("Identity");
        sysInfo.Elements.SetInteger("/Supplement", 0);
        cidFont.Elements["/CIDSystemInfo"] = sysInfo;
        cidFont.Elements["/FontDescriptor"] = descriptor.Reference!;
        cidFont.Elements.SetInteger("/DW", 1000);
        cidFont.Elements["/W"] = wArr;
        doc.Internals.AddObject(cidFont);

        var toUni = new PdfDictionary(doc);
        toUni.CreateStream(Encoding.ASCII.GetBytes(BuildToUnicode(toUnicode)));
        doc.Internals.AddObject(toUni);

        var type0 = new PdfDictionary(doc);
        type0.Elements.SetName("/Type", "/Font");
        type0.Elements.SetName("/Subtype", "/Type0");
        type0.Elements.SetName("/BaseFont", "/" + baseFont);
        type0.Elements.SetName("/Encoding", "/Identity-H");
        type0.Elements["/DescendantFonts"] = new PdfArray(doc, cidFont.Reference!);
        type0.Elements["/ToUnicode"] = toUni.Reference!;
        doc.Internals.AddObject(type0);
        return type0.Reference!;
    }

    /// <summary>
    /// TrueType 서브셋이 화면이 쓴 글리프 번호를 모두 담고 있는지 확인한다(글리프 번호 유지 서브셋이므로 번호가 같아야 한다).
    /// 서브셋의 글리프 수가 쓴 번호보다 작으면 InvalidOperationException.
    /// </summary>
    private static void VerifyGids(AppFont font, FontUse use, byte[] sub)
    {
        if (font.HasCidCharset) throw new InvalidOperationException("TrueType font unexpectedly has a CFF charset.");
        int numGlyphs = NumGlyphs(sub);
        foreach (var g in use.Glyphs)
            if (g.Cid >= numGlyphs)
                throw new InvalidOperationException($"Glyph {g.Cid} is not in the TrueType subset ({numGlyphs} glyphs); glyph ids differ from the full font.");
    }

    /// <summary>maxp 표의 numGlyphs. 없으면 0.</summary>
    internal static int NumGlyphs(byte[] otf)
    {
        int numTables = (otf[4] << 8) | otf[5];
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            if (otf[rec] == 'm' && otf[rec + 1] == 'a' && otf[rec + 2] == 'x' && otf[rec + 3] == 'p')
            {
                int off = (otf[rec + 8] << 24) | (otf[rec + 9] << 16) | (otf[rec + 10] << 8) | otf[rec + 11];
                return (otf[off + 4] << 8) | otf[off + 5];
            }
        }
        return 0;
    }

    /// <summary>PDF 이름으로 써도 되는 PostScript 이름(인쇄 가능한 ASCII, 구분 문자 없음, 63자 이하). 아니면 null.</summary>
    private static string? SafeName(string? n) =>
        n is { Length: > 0 and <= 63 } && n.All(c => c is > ' ' and < (char)127 and not ('/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%' or '#'))
            ? n : null;

    /// <summary>서브셋의 CID가 전체 글꼴의 CID와 같은지 확인한다(V11). 다르면 InvalidOperationException.</summary>
    private static void VerifyCids(AppFont font, FontUse use, int[] cps, byte[] sub, int[]? subCidByGid)
    {
        RequireCidKeyed(font.HasCidCharset, subCidByGid);
        if (subCidByGid is null) return;   // 도달하지 않음 (RequireCidKeyed가 던진다)

        var present = new HashSet<int>(subCidByGid);
        foreach (int cp in cps)
        {
            if (font.GetGlyph(cp) is not { } gid) continue;   // 글꼴에 없는 글자는 건너뜀(배치 단계에서 이미 알림)
            int fullCid = font.GetCid(gid);
            if (!present.Contains(fullCid))
                throw new InvalidOperationException($"Subset lost CID {fullCid} for U+{cp:X4}; subset CIDs differ from the full font.");
        }
        foreach (var g in use.Glyphs)
            if (g.Cid != 0 && !present.Contains(g.Cid))
                throw new InvalidOperationException($"Glyph CID {g.Cid} is not in the subset; subset CIDs differ from the full font.");
    }

    /// <summary>
    /// 전체 글꼴과 서브셋이 모두 CID 키 방식 CFF여야 한다. 이름 키 방식은 CID = 글리프 번호인데 서브셋이 번호를 다시 매기므로
    /// 화면과 같은 CID를 보장할 수 없어 지원하지 않는다(조용히 넘어가지 않고 던진다).
    /// </summary>
    internal static void RequireCidKeyed(bool fullFontCidKeyed, int[]? subsetCidByGid)
    {
        if (fullFontCidKeyed != (subsetCidByGid is not null))
            throw new InvalidOperationException("Subset CFF charset kind differs from the full font (CID-keyed vs name-keyed).");
        if (subsetCidByGid is null)
            throw new InvalidOperationException("Name-keyed CFF fonts are not supported: CIDs cannot be guaranteed after subsetting.");
    }

    private static PdfArray Arr(PdfSharp.Pdf.PdfDocument doc, params double[] v) =>
        new(doc, v.Select(d => (PdfItem)new PdfReal(d)).ToArray());

    private static string SubsetTag(int[] cps)
    {
        int h = 17;
        foreach (var c in cps) h = unchecked(h * 31 + c);
        var sb = new StringBuilder();
        uint u = (uint)h;
        for (int i = 0; i < 6; i++) { sb.Append((char)('A' + u % 26)); u /= 26; }
        return sb.ToString();
    }

    private static string BuildToUnicode(SortedDictionary<int, string> map)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var chunk in map.Chunk(100))
        {
            sb.Append(chunk.Length).Append(" beginbfchar\n");
            foreach (var (cid, s) in chunk)
            {
                sb.Append('<').Append(cid.ToString("X4")).Append("> <");
                foreach (var b in Encoding.BigEndianUnicode.GetBytes(s)) sb.Append(b.ToString("X2"));
                sb.Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }
}
