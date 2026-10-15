using System.Drawing;
using System.Globalization;
using System.Security;
using System.Text;
using HarfBuzzSharp;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using S2.FontSubset;

namespace S1.FreeTextKorean;

/// <param name="RectPt">페이지 왼쪽 위가 원점인 점(pt) 좌표 (x, y, 폭, 높이)</param>
public record FreeTextSpec(int PageIndex, RectangleF RectPt, string Text, float FontSizePt, uint ColorArgb);

/// <summary>
/// 경로 B: PDFium이 저장한 파일을 PDFsharp으로 다시 열어 FreeText 주석과 AP(한글 서브셋 폰트 포함)를 기록한다.
/// </summary>
public static class FreeTextApWriter
{
    public static void Apply(string inputPdf, string outputPdf, FreeTextSpec spec, byte[] fontData)
    {
        var lines = spec.Text.Replace("\r\n", "\n").Split('\n');
        var cps = spec.Text.EnumerateRunes().Select(r => r.Value).Where(v => v != '\n' && v != '\r')
            .Append(' ').Distinct().ToArray();
        byte[] sub = FontSubsetter.Subset(fontData, cps);
        int[]? cidByGid = CffCharset.GetCidByGid(sub);

        using var blob = Blob.FromStream(new MemoryStream(sub));
        using var face = new Face(blob, 0);
        using var font = new HarfBuzzSharp.Font(face);
        int upem = face.UnitsPerEm;
        font.SetScale(upem, upem);
        var ext = font.GetFontExtentsForDirection(Direction.LeftToRight);

        // 줄마다 셰이핑 → CID 열과 폭 수집
        var widths = new SortedDictionary<int, int>();       // cid -> 폭(1/1000 em)
        var toUnicode = new SortedDictionary<int, string>(); // cid -> 문자
        var lineCids = new List<int[]>();
        foreach (var line in lines)
        {
            using var buf = new HarfBuzzSharp.Buffer();
            buf.AddUtf16(line);
            buf.GuessSegmentProperties();
            font.Shape(buf);
            var infos = buf.GlyphInfos;
            var pos = buf.GlyphPositions;
            var cids = new int[infos.Length];
            for (int i = 0; i < infos.Length; i++)
            {
                int gid = (int)infos[i].Codepoint;
                int cid = cidByGid is null ? gid : cidByGid[gid];
                cids[i] = cid;
                widths[cid] = (int)Math.Round(pos[i].XAdvance * 1000.0 / upem);
                int ci = (int)infos[i].Cluster;
                if (ci < line.Length && !toUnicode.ContainsKey(cid))
                    toUnicode[cid] = char.IsHighSurrogate(line[ci]) && ci + 1 < line.Length
                        ? line.Substring(ci, 2) : line[ci].ToString();
            }
            lineCids.Add(cids);
        }

        string tag = SubsetTag(cps);
        string baseFont = $"{tag}+NotoSansKR-Regular";

        using var doc = PdfReader.Open(inputPdf, PdfDocumentOpenMode.Modify);
        var page = doc.Pages[spec.PageIndex];
        double pageH = page.Height.Point;
        var r = spec.RectPt;
        double x1 = r.X, x2 = r.X + r.Width, y1 = pageH - (r.Y + r.Height), y2 = pageH - r.Y;

        // 폰트 객체들
        var fontFile = new PdfDictionary(doc);
        fontFile.CreateStream(CffCharset.ExtractCff(sub));
        fontFile.Elements.SetName("/Subtype", "/CIDFontType0C");
        doc.Internals.AddObject(fontFile);

        double asc = ext.Ascender * 1000.0 / upem, desc = ext.Descender * 1000.0 / upem;
        var descriptor = new PdfDictionary(doc);
        descriptor.Elements.SetName("/Type", "/FontDescriptor");
        descriptor.Elements.SetName("/FontName", "/" + baseFont);
        descriptor.Elements.SetInteger("/Flags", 4);
        descriptor.Elements["/FontBBox"] = Arr(doc, 0, desc, 1000, asc);
        descriptor.Elements.SetInteger("/ItalicAngle", 0);
        descriptor.Elements.SetInteger("/Ascent", (int)asc);
        descriptor.Elements.SetInteger("/Descent", (int)desc);
        descriptor.Elements.SetInteger("/CapHeight", (int)(asc * 0.8));
        descriptor.Elements.SetInteger("/StemV", 80);
        descriptor.Elements["/FontFile3"] = fontFile.Reference ?? throw new InvalidOperationException();
        doc.Internals.AddObject(descriptor);

        var wArr = new PdfArray(doc);
        foreach (var (cid, w) in widths)
        {
            wArr.Elements.Add(new PdfInteger(cid));
            wArr.Elements.Add(Arr(doc, w));
        }
        var cidFont = new PdfDictionary(doc);
        cidFont.Elements.SetName("/Type", "/Font");
        cidFont.Elements.SetName("/Subtype", "/CIDFontType0");
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

        // AP 폼
        double w0 = r.Width, h0 = r.Height, fs = spec.FontSizePt;
        float cr = ((spec.ColorArgb >> 16) & 0xFF) / 255f, cg = ((spec.ColorArgb >> 8) & 0xFF) / 255f, cb = (spec.ColorArgb & 0xFF) / 255f;
        var cs = new StringBuilder();
        cs.Append(CultureInfo.InvariantCulture, $"q\n{cr:0.###} {cg:0.###} {cb:0.###} rg\nBT\n/F1 {fs:0.###} Tf\n");
        double lineH = fs * 1.3, baseline = h0 - 2 - fs * asc / 1000.0;
        for (int i = 0; i < lineCids.Count; i++)
        {
            double dy = i == 0 ? baseline : -lineH;
            cs.Append(FormattableString.Invariant($"{(i == 0 ? 2 : 0)} {dy:0.###} Td\n"));
            cs.Append('<').Append(string.Concat(lineCids[i].Select(c => c.ToString("X4")))).Append("> Tj\n");
        }
        cs.Append("ET\nQ\n");

        var fontRes = new PdfDictionary(doc);
        fontRes.Elements["/F1"] = type0.Reference!;
        var res = new PdfDictionary(doc);
        res.Elements["/Font"] = fontRes;
        var form = new PdfDictionary(doc);
        form.CreateStream(Encoding.ASCII.GetBytes(cs.ToString()));
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = Arr(doc, 0, 0, w0, h0);
        form.Elements["/Resources"] = res;
        doc.Internals.AddObject(form);

        // 주석 딕셔너리
        var annot = new PdfDictionary(doc);
        annot.Elements.SetName("/Type", "/Annot");
        annot.Elements.SetName("/Subtype", "/FreeText");
        annot.Elements["/Rect"] = Arr(doc, x1, y1, x2, y2);
        annot.Elements["/Contents"] = new PdfString(spec.Text, PdfStringEncoding.Unicode);
        annot.Elements["/DA"] = new PdfString(string.Create(CultureInfo.InvariantCulture, $"{cr:0.###} {cg:0.###} {cb:0.###} rg /Helv {fs:0.###} Tf"));
        annot.Elements["/RC"] = new PdfString(BuildRichText(spec, cr, cg, cb), PdfStringEncoding.Unicode);
        annot.Elements.SetInteger("/F", 4);
        annot.Elements.SetInteger("/Q", 0);
        annot.Elements["/NM"] = new PdfString(Guid.NewGuid().ToString());
        annot.Elements["/M"] = new PdfString("D:" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        var apDict = new PdfDictionary(doc);
        apDict.Elements["/N"] = form.Reference!;
        annot.Elements["/AP"] = apDict;
        doc.Internals.AddObject(annot);

        PdfArray annots = page.Elements["/Annots"] switch
        {
            PdfArray a => a,
            PdfReference { Value: PdfArray a2 } => a2,
            _ => null!,
        };
        if (annots is null)
        {
            annots = new PdfArray(doc);
            page.Elements["/Annots"] = annots;
        }
        annots.Elements.Add(annot.Reference!);

        doc.Save(outputPdf);
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

    private static string BuildRichText(FreeTextSpec s, float r, float g, float b)
    {
        string hex = $"#{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
        var paras = string.Concat(s.Text.Replace("\r\n", "\n").Split('\n')
            .Select(l => $"<p dir=\"ltr\">{SecurityElement.Escape(l)}</p>"));
        return "<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\" " +
               $"style=\"font-size:{s.FontSizePt.ToString(CultureInfo.InvariantCulture)}pt;text-align:left;color:{hex};font-family:Noto Sans KR\">{paras}</body>";
    }
}
