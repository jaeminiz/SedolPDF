using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Pdf.Native;
using PdfEditor.ContentStream;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SharpPage = PdfSharp.Pdf.PdfPage;

namespace PdfEditor.TextEdit.Tests;

// V20: 직접 인코딩 쓰기 충실도 + 지운 글 완전 제거(C12). V17ToV21Probes의 나머지 절반.
public sealed partial class V17ToV21Probes
{
    private static byte[] RenderPage(PdfiumThread t, string path, double scale = 2)
    {
        return t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(t, path, null);
            using var p = d.LoadPage(0);
            var sz = p.SizePt;
            int w = (int)Math.Ceiling(sz.Width * scale), h = (int)Math.Ceiling(sz.Height * scale);
            return p.RenderTile(scale, 0, 0, w, h, withAnnotations: false);
        }).GetAwaiter().GetResult();
    }

    /// <summary>두 그림에서 (어느 한쪽이라도 잉크인 칸) 중 서로 크게 다른 칸의 비율(%).</summary>
    private static (double DiffPct, int Ink) InkDiff(byte[] a, byte[] b)
    {
        int ink = 0, diff = 0;
        for (int i = 0; i + 3 < a.Length && i + 3 < b.Length; i += 4)
        {
            bool ia = a[i] < 200 || a[i + 1] < 200 || a[i + 2] < 200, ib = b[i] < 200 || b[i + 1] < 200 || b[i + 2] < 200;
            if (!ia && !ib) continue;
            ink++;
            if (Math.Abs(a[i] - b[i]) > 64 || Math.Abs(a[i + 1] - b[i + 1]) > 64 || Math.Abs(a[i + 2] - b[i + 2]) > 64) diff++;
        }
        return (ink == 0 ? 0 : 100.0 * diff / ink, ink);
    }

    private static void RewritePage(string src, string dst, Func<SharpPage, byte[]?> mutate)
    {
        using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
        var page = doc.Pages[0];
        var nb = mutate(page);
        if (nb is not null) { page.Contents.Elements.Clear(); page.Contents.AppendContent().CreateStream(nb); }
        doc.Save(dst);
    }

    private static string Slice(byte[] d, ContentOp op) => Encoding.Latin1.GetString(d, op.Start, op.End - op.Start);
    private static string F(double v) => v.ToString("0.########", CultureInfo.InvariantCulture);

    /// <summary>
    /// textOrdinal번째 글 객체를 같은 글·같은 자리에 코드북으로 다시 인코딩해 덧붙인다(원래 객체의 보이기 연산자는 통째로 지움).
    /// Content가 null이면 Note에 이유. tc/tz/justify로 자간·가로 비율·TJ 맞춤을 시험한다(공백마다 +3 pt).
    /// </summary>
    private static (byte[]? Content, string Note) RewriteOneText(SharpPage page, int textOrdinal, double tc, double tz, bool justify)
    {
        var loaded = PageContent.Load(page);
        var pc = ContentLexer.Parse(loaded.Data!);
        var seg = ContentSegmenter.Segment(pc, PageContent.XObjectResolver(page));
        // textOrdinal번째로 글이 긴(보이기 연산자의 글 바이트 수) 객체
        int ByteLen(PageObject x) { int n = 0; void A(Operand y) { if (y.Kind == OperandKind.String) n += y.Bytes.Length; else if (y.Kind == OperandKind.Array) foreach (var z in y.Items) A(z); } foreach (var y in pc.Ops[x.OpIndex].Operands) A(y); return n; }
        var texts = seg.Objects.Where(o => o.Kind == ObjectKind.Text).OrderByDescending(ByteLen).ToList();
        if (textOrdinal >= texts.Count) return (null, "no such text");
        var o = texts[textOrdinal];
        var snap = o.Text!;
        if (!snap.Movable) return (null, "not movable");
        var tf = pc.Ops[snap.FontOp];
        string name = tf.Operands.First(x => x.Kind == OperandKind.Name).Text;
        var fd = page.Resources!.Elements.GetDictionary("/Font")!.Elements.GetDictionary("/" + name)!;
        var (book, why) = BookBuilder.Build(fd, relaxed: true);
        if (book is null) return (null, "no book: " + why);
        var show = pc.Ops[o.OpIndex];
        var pieces = new List<byte[]>();
        void Add(Operand x) { if (x.Kind == OperandKind.String) pieces.Add(x.Bytes); else if (x.Kind == OperandKind.Array) foreach (var y in x.Items) Add(y); }
        foreach (var x in show.Operands) Add(x);
        var all = pieces.SelectMany(x => x).ToArray();
        string text = book.Decode(all);
        var codes = new List<int>();
        foreach (char ch in text)
        {
            if (!book.CharToCode.TryGetValue(ch, out int code)) return (null, $"char U+{(int)ch:X4} not encodable");
            codes.Add(code);
        }
        byte[] Enc(IEnumerable<int> cs) => book.TwoByte ? cs.SelectMany(c => new[] { (byte)(c >> 8), (byte)c }).ToArray() : cs.Select(c => (byte)c).ToArray();
        string Hex(byte[] b) => "<" + Convert.ToHexString(b) + ">";
        var enc = Enc(codes);
        if (!enc.SequenceEqual(all)) return (null, "round trip differs");
        var pageTm = snap.Tm.Then(o.Ctm);
        var sb = new StringBuilder("\n");
        foreach (int k in snap.ReplayOps) sb.Append(Slice(loaded.Data!, pc.Ops[k])).Append(' ');
        sb.Append(Slice(loaded.Data!, tf)).Append(' ');
        sb.Append($"{F(tc)} Tc {F(snap.Tw)} Tw {F(tz)} Tz 0 Ts {snap.Tr} Tr BT {pageTm.ToOperands()} Tm ");
        if (justify)
        {
            double sizeRaw = tf.Operands.Last(x => x.Kind == OperandKind.Number).Number, hscale = Math.Sqrt(pageTm.A * pageTm.A + pageTm.B * pageTm.B);
            var parts = new List<string>(); var cur = new List<int>();
            int idx = 0;
            foreach (char ch in text)
            {
                cur.Add(codes[idx++]);
                if (ch == ' ') { parts.Add(Hex(Enc(cur))); parts.Add(F(-(3.0 / hscale) / sizeRaw * 1000.0)); cur.Clear(); }
            }
            if (cur.Count > 0) parts.Add(Hex(Enc(cur)));
            sb.Append('[').Append(string.Join(' ', parts)).Append("] TJ");
        }
        else sb.Append(Hex(enc)).Append(" Tj");
        sb.Append(" ET\n");
        var data = loaded.Data!;
        var ms = new MemoryStream();
        ms.Write(data, 0, show.Start); ms.Write(data, show.End, data.Length - show.End);
        var pc2 = ContentLexer.Parse(ms.ToArray());
        var res = ContentRewriter.AppendOnly(pc2, [Encoding.Latin1.GetBytes(sb.ToString())]);
        return (res.Data, res.Failure ?? "ok");
    }

    private static PdfEditor.Document.Annotations.Patching.GlyphUse SpaceGlyph(PdfEditor.Appearance.Fonts.AppFont font)
    {
        int gid = font.GetGlyph(32) ?? 0;
        return new PdfEditor.Document.Annotations.Patching.GlyphUse(font.GetCid(gid), font.Advance1000(gid), " ");
    }

    private static string? BuildCffSample(string dir)
    {
        Directory.CreateDirectory(dir);
        string? fontPath = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "assets", "fonts", "NotoSansKR-Regular.otf");
            if (File.Exists(p)) { fontPath = p; break; }
        }
        if (fontPath is null) return null;
        using var font = PdfEditor.Appearance.Fonts.AppFont.Load(fontPath);
        string path = Path.Combine(dir, "cff-sample.pdf");
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage(); page.Width = PdfSharp.Drawing.XUnit.FromPoint(595); page.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
        var res = new PdfDictionary(doc); page.Elements["/Resources"] = res;
        var fonts = new PdfDictionary(doc); res.Elements["/Font"] = fonts;
        var lines = new[] { "도면 검토 절차는 먼저 접수된 도면의 개정 이력을 확인하고", "Valve actuators are tested at rated pressure", "배관 지지대 위치 검사 기록 123" };
        float y = 740;
        var specs = new List<PdfEditor.Document.Content.AppTextSpec>();
        foreach (var l in lines)
        {
            var (spec, _) = AppFontLine.Build(font, l, 12, new PdfMatrix(1, 0, 0, 1, 60, y), new PdfEditor.Document.Annotations.PdfColor(0, 0, 0));
            if (spec is null) return null;
            specs.Add(spec); y -= 30;
        }
        var allUse = new PdfEditor.Document.Annotations.Patching.FontUse(
            ImmutableArray.CreateRange(specs.SelectMany(s => (IEnumerable<int>)s.Font.Codepoints).Distinct()),
            ImmutableArray.CreateRange(specs.SelectMany(s => (IEnumerable<PdfEditor.Document.Annotations.Patching.GlyphUse>)s.Font.Glyphs).Append(SpaceGlyph(font))));
        fonts.Elements["/SedolFn"] = PdfEditor.Appearance.Fonts.FontEmbedder.Embed(doc, font, allUse);
        var content = new StringBuilder();
        foreach (var s in specs) content.Append(s.Content.Replace("{F}", "SedolFn"));
        // 여러 글자를 한 Tj로 쓴 CFF 글(실제 도면 PDF의 모양): 글 코드는 CID, 2바이트
        var cidOf = new Dictionary<char, int>();
        foreach (var g in allUse.Glyphs) if (g.Text.Length == 1) cidOf.TryAdd(g.Text[0], g.Cid);
        foreach (var (line, ly) in new[] { ("Valve actuators are tested at rated pressure", 560), ("Valve is tested at rated", 530) })
        {
            if (!line.All(c => cidOf.ContainsKey(c))) continue;
            string hex = string.Concat(line.Select(c => cidOf[c].ToString("X4")));
            content.Append($"BT /SedolFn 12 Tf 1 0 0 1 60 {ly} Tm <{hex}> Tj ET\n");
        }
        page.Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes(content.ToString()));
        doc.Save(path);
        return path;
    }

    [Fact]
    public void V20_DirectEncoding_RenderFidelity()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== V20-a 직접 인코딩: 같은 글을 코드북으로 다시 인코딩해 같은 자리에 덧붙이고(원래 객체는 연산자째 제거) PDFium으로 렌더 비교 (plain: 차이 칸 / 잉크 칸 ≤ 1 % 가 합격)");
        string dir = Path.Combine(Path.GetTempPath(), "m5-v20");
        Directory.CreateDirectory(dir);
        var cases = new List<(string Name, string Path)>();
        foreach (var n in new[] { "doc-en-left", "doc-ko-left-wordwrap", "doc-en-winansi", "doc-std14", "doc-differences" })
            cases.Add((n, Samples().First(s => s.Name == n).Path));
        string? cff = BuildCffSample(dir);
        if (cff is not null) cases.Add(("cff-cid-notosans", cff)); else sb.AppendLine("CFF 샘플: 앱 글꼴 파일을 찾지 못함");
        int pass = 0, total = 0;
        foreach (var (name, path) in cases)
        {
            using var probeDoc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            var fonts = probeDoc.Pages[0].Resources?.Elements.GetDictionary("/Font");
            string kinds = fonts is null ? "-" : string.Join("|", fonts.Elements.Keys.Select(k => BookBuilder.KindOf(fonts.Elements.GetDictionary(k)!)).Distinct());
            var orig = RenderPage(_t, path);
            foreach (var (label, tc, tz, just) in new[] { ("plain", 0.0, 100.0, false), ("Tc0.5", 0.5, 100.0, false), ("Tz90", 0.0, 90.0, false), ("TJ-justify", 0.0, 100.0, true) })
                for (int ord = 0; ord < 2; ord++)
                {
                    string dst = Path.Combine(dir, $"{name}-{label}-{ord}.pdf");
                    string note = "";
                    RewritePage(path, dst, page => { var (c, n) = RewriteOneText(page, ord, tc, tz, just); note = n; return c; });
                    if (note != "ok") { sb.AppendLine($"{name,-22} {label,-10} #{ord}: 건너뜀 ({note}) fonts=[{kinds}]"); continue; }
                    var re = RenderPage(_t, dst);
                    var (pct, ink) = InkDiff(orig, re);
                    if (label == "plain") { total++; if (pct <= 1.0) pass++; }
                    sb.AppendLine($"{name,-22} {label,-10} #{ord}: 차이 {pct:0.00}% (잉크 칸 {ink}) {(label == "plain" ? (pct <= 1.0 ? "PASS" : "FAIL") : "(참고: 원본과 일부러 다르게 쓴 것)")}  fonts=[{kinds}]");
                }
        }
        sb.AppendLine($"plain 재현 합격 {pass}/{total}");
        File.WriteAllText(Out("m5-v20-fidelity.txt"), sb.ToString());
        Assert.True(total > 0);
    }

    /// <summary>자간·가로 비율·TJ 조정이 코드북 공식과 PDFium 결과에서 같은 폭 변화를 주는가(같은 글, 기본 대비 폭 차이).</summary>
    [Fact]
    public void V20_TcTzTJ_WidthDeltas_MatchFormula()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== V20-b 자간(Tc)·가로 비율(Tz)·TJ 맞춤: 기본(plain) 대비 폭 변화 — PDFium 객체 폭 vs 공식(허용 0.3 pt + 0.5 %)");
        string dir = Path.Combine(Path.GetTempPath(), "m5-v20b");
        Directory.CreateDirectory(dir);
        foreach (var srcPath in new[] { Samples().First(s => s.Name == "doc-en-left").Path, BuildCffSample(Path.Combine(Path.GetTempPath(), "m5-v20b-cff")) })
        {
        if (srcPath is null) continue;
        string src = srcPath;
        int ok = 0, all = 0;
        double basew = 0;
        // 기본
        string basePath = Path.Combine(dir, "base.pdf");
        RewritePage(src, basePath, page => RewriteOneText(page, 0, 0, 100, false).Content);
        var baseRun = ParagraphProbe.ReadPdfium(_t, basePath, 0).Runs.Last();
        basew = baseRun.U1 - baseRun.U0;
        int nChars = baseRun.Text.Length, nSpaces = baseRun.Text.Count(c => c == ' ');
        foreach (var (label, tc, tz, just, predicted) in new[]
        {
            ("Tc +0.5", 0.5, 100.0, false, 0.5 * (nChars - 1)),
            ("Tc -0.3", -0.3, 100.0, false, -0.3 * (nChars - 1)),
            ("Tz 90", 0.0, 90.0, false, basew * -0.10),
            ("Tz 115", 0.0, 115.0, false, basew * 0.15),
            ("TJ +3pt/space", 0.0, 100.0, true, 3.0 * nSpaces),
        })
        {
            string p = Path.Combine(dir, label.Replace(' ', '_').Replace('/', '_') + ".pdf");
            RewritePage(src, p, page => RewriteOneText(page, 0, tc, tz, just).Content);
            var r = ParagraphProbe.ReadPdfium(_t, p, 0).Runs.Last();
            double w = r.U1 - r.U0, delta = w - basew;
            bool good = Math.Abs(delta - predicted) <= 0.3 + 0.005 * basew;
            all++; if (good) ok++;
            sb.AppendLine($"{label,-14}: 폭 변화 실제 {delta:0.00} pt, 공식 {predicted:0.00} pt (기본 폭 {basew:0.0}, 글자 {nChars}, 공백 {nSpaces}) {(good ? "PASS" : "FAIL")}");
        }
        sb.AppendLine($"합격 {ok}/{all} ({Path.GetFileName(src)})");
        Assert.True(all > 0);
        }
        File.WriteAllText(Out("m5-v20-tctztj.txt"), sb.ToString());
    }

    // ---- 지운 글 완전 제거 (C12) ----
    internal sealed record Removal(byte[] Data, bool Deleted, string Why);

    /// <summary>
    /// 글 객체 objIdx의 보이기 연산자를 지운다. 바로 뒤의 글 보이기가 Tm/Td/TD/T*/'/"로 자리를 새로 잡거나 ET/BT가 먼저 오면 연산자째 삭제(Delete),
    /// 아니면(뒤 글이 이 글의 이동량에 기대면) 숨김(Hide: 3 Tr)으로 내려간다. '·" 연산자는 줄 이동(T*)을 남긴다.
    /// </summary>
    internal static Removal RemoveText(byte[] data, ParsedContent pc, Segmentation seg, int objIdx)
    {
        var o = seg.Objects[objIdx];
        var ops = pc.Ops;
        var op = ops[o.OpIndex];
        bool safe = true; string why = "end of content";
        for (int j = o.OpIndex + 1; j < ops.Count; j++)
        {
            string n = ops[j].Name;
            if (n is "Tm" or "Td" or "TD" or "T*" or "'" or "\"") { safe = true; why = "next text repositions via " + n; break; }
            if (n is "ET" or "BT") { safe = true; why = "text object ends (" + n + ")"; break; }
            if (n is "Tj" or "TJ") { safe = false; why = "next text continues from this text's advance"; break; }
        }
        var ms = new MemoryStream();
        if (safe)
        {
            ms.Write(data, 0, op.Start);
            if (op.Name == "'") ms.Write(Encoding.Latin1.GetBytes("T*"));
            else if (op.Name == "\"")
            {
                double aw = op.Operands.Count >= 3 ? op.Operands[0].Number : 0, ac = op.Operands.Count >= 3 ? op.Operands[1].Number : 0;
                ms.Write(Encoding.Latin1.GetBytes($"{F(aw)} Tw {F(ac)} Tc T*"));
            }
            ms.Write(data, op.End, data.Length - op.End);
            return new Removal(ms.ToArray(), true, why);
        }
        ms.Write(data, 0, op.Start);
        ms.Write(Encoding.Latin1.GetBytes("3 Tr "));
        ms.Write(data, op.Start, op.End - op.Start);
        ms.Write(Encoding.Latin1.GetBytes($" {o.Text!.Tr} Tr"));
        ms.Write(data, op.End, data.Length - op.End);
        return new Removal(ms.ToArray(), false, why);
    }

    private static string MakeRawPdf(string path, string content)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage(); page.Width = PdfSharp.Drawing.XUnit.FromPoint(595); page.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
        var res = new PdfDictionary(doc); page.Elements["/Resources"] = res;
        var fonts = new PdfDictionary(doc); res.Elements["/Font"] = fonts;
        var f = new PdfDictionary(doc);
        f.Elements["/Type"] = new PdfName("/Font"); f.Elements["/Subtype"] = new PdfName("/Type1");
        f.Elements["/BaseFont"] = new PdfName("/Helvetica"); f.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        doc.Internals.AddObject(f);
        fonts.Elements["/F1"] = f.Reference!;
        page.Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes(content));
        doc.Save(path);
        return path;
    }

    [Fact]
    public async Task V20_RemovedText_NotLeftInFile_And_FollowingTextStays()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== V20-c 지운 글 완전 제거(C12): 뒤 글 위치 불변 + 저장된 파일에서 옛 글이 안 나옴(Delete) / 숨김(Hide)으로 내려가는 경우");
        string dir = Path.Combine(Path.GetTempPath(), "m5-v20c");
        Directory.CreateDirectory(dir);
        var cases = new (string Name, string Content, int Remove, bool ExpectDelete)[]
        {
            ("separate BT with Tm", "BT /F1 12 Tf 1 0 0 1 50 700 Tm (AAA first) Tj ET BT /F1 12 Tf 1 0 0 1 50 680 Tm (BBB second) Tj ET BT /F1 12 Tf 1 0 0 1 50 660 Tm (CCC third) Tj ET", 1, true),
            ("Td relative lines", "BT /F1 12 Tf 50 700 Td (AAA first) Tj 0 -20 Td (BBB second) Tj 0 -20 Td (CCC third) Tj ET", 1, true),
            ("advance chain (middle)", "BT /F1 12 Tf 50 700 Td (AAA first ) Tj (BBB second ) Tj (CCC third) Tj ET", 1, false),
            ("advance chain (first)", "BT /F1 12 Tf 50 700 Td (AAA first ) Tj (BBB second ) Tj ET", 0, false),
            ("quote operator", "BT /F1 12 Tf 20 TL 50 700 Td (AAA first) Tj (BBB second) ' (CCC third) ' ET", 1, true),
            ("last before ET", "BT /F1 12 Tf 50 700 Td (AAA first) Tj 0 -20 Td (BBB second) Tj ET BT /F1 12 Tf 1 0 0 1 50 640 Tm (CCC third) Tj ET", 1, true),
            ("TJ array", "BT /F1 12 Tf 50 700 Td [(AAA) 20 ( first)] TJ 0 -20 Td [(BBB) -30 ( second)] TJ 0 -20 Td (CCC third) Tj ET", 1, true),
            ("T* newline", "BT /F1 12 Tf 20 TL 50 700 Td (AAA first) Tj T* (BBB second) Tj T* (CCC third) Tj ET", 1, true),
        };
        int good = 0;
        foreach (var (name, content, remove, expectDelete) in cases)
        {
            string src = MakeRawPdf(Path.Combine(dir, "src.pdf"), content);
            using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Import);
            var page = doc.Pages[0];
            var loaded = PageContent.Load(page);
            var pc = ContentLexer.Parse(loaded.Data!);
            var seg = ContentSegmenter.Segment(pc, PageContent.XObjectResolver(page));
            var rm = RemoveText(loaded.Data!, pc, seg, remove);
            string dst = Path.Combine(dir, "dst.pdf");
            RewritePage(src, dst, _ => rm.Data);
            var before = ParagraphProbe.ReadPdfium(_t, src, 0).Runs;
            var after = ParagraphProbe.ReadPdfium(_t, dst, 0).Runs;   // 보이는 글만
            var removedRun = before[remove];
            string removedWord = removedRun.Trim.Split(' ')[0];
            bool othersStay = before.Where((r, i) => i != remove).All(r => after.Any(a => a.Trim == r.Trim && Math.Abs(a.OX - r.OX) < 0.01 && Math.Abs(a.OY - r.OY) < 0.01));
            bool goneFromVisible = !after.Any(a => a.Trim == removedRun.Trim);
            bool bytesGone = !Encoding.Latin1.GetString(rm.Data).Contains(removedWord);
            // 추출(PDFium 텍스트): 숨긴 글은 보이지 않는 글이어도 추출될 수 있다 — 전체 글에서 확인
            string extracted = await _t.InvokeAsync(() =>
            {
                using var d = Pdf.Native.PdfDocument.Open(_t, dst, null);
                using var p = d.LoadPage(0); using var tp = p.LoadText();
                return tp.GetText(0, tp.CharCount);
            });
            bool extractGone = !extracted.Contains(removedWord);
            bool pass = othersStay && goneFromVisible && rm.Deleted == expectDelete && (!rm.Deleted || (bytesGone && extractGone));
            if (pass) good++;
            sb.AppendLine($"{name,-24} 방식={(rm.Deleted ? "Delete" : "Hide  ")} (예상 {(expectDelete ? "Delete" : "Hide")}) 뒤 글 위치 불변={othersStay} 화면에서 사라짐={goneFromVisible} 바이트에 옛 글 없음={bytesGone} 추출에 옛 글 없음={extractGone} → {(pass ? "PASS" : "FAIL")}  ({rm.Why})");
        }
        sb.AppendLine($"합격 {good}/{cases.Length}");
        File.WriteAllText(Out("m5-v20-removal.txt"), sb.ToString());
        Assert.Equal(cases.Length, good);
    }

    /// <summary>실제·합성 쪽의 글 객체 전체에 같은 제거 규칙을 적용해 보았을 때 Delete로 갈 수 있는 비율(분류만, 쓰기 없음).</summary>
    [Fact]
    public void V20_RemovalEligibility_Statistics()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== V20-d 지운 글 제거 규칙: 글 객체 중 연산자째 삭제(Delete) 가능한 비율 — 분류만(실제 PDF는 앞 5쪽)");
        var srcs = new List<(string Label, string Path, int Pages)>();
        foreach (var s in Samples()) srcs.Add(("syn:" + s.Name, s.Path, 1));
        foreach (var s in RealSources()) srcs.Add((s.Label, s.Path, RealPages));
        foreach (var grp in srcs.GroupBy(x => x.Label.StartsWith("syn:") ? "synthetic" : x.Label[..8]))
        {
            int del = 0, hide = 0, noSeg = 0;
            foreach (var (label, path, pages) in grp)
            {
                using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                for (int p = 0; p < Math.Min(pages, doc.PageCount); p++)
                {
                    var loaded = PageContent.Load(doc.Pages[p]);
                    if (loaded.Data is null) { noSeg++; continue; }
                    var pc = ContentLexer.Parse(loaded.Data);
                    var seg = ContentSegmenter.Segment(pc, PageContent.XObjectResolver(doc.Pages[p]));
                    if (!seg.Ok) { noSeg++; continue; }
                    for (int i = 0; i < seg.Objects.Count; i++)
                    {
                        if (seg.Objects[i].Kind != ObjectKind.Text) continue;
                        var rm = RemoveText(loaded.Data, pc, seg, i);
                        if (rm.Deleted) del++; else hide++;
                    }
                }
            }
            sb.AppendLine($"{grp.Key}: 글 객체 {del + hide}개 중 Delete 가능 {del} ({(del + hide == 0 ? 0 : 100.0 * del / (del + hide)):0.#}%), Hide 필요 {hide}, 해석 실패 쪽 {noSeg}");
        }
        File.WriteAllText(Out("m5-v20-removal-stats.txt"), sb.ToString());
    }
}
