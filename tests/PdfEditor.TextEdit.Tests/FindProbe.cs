using System.Text;
using PdfEditor.ContentStream;
using PdfEditor.ContentStream.Fonts;
using PdfSharp.Pdf;
using SharpPage = PdfSharp.Pdf.PdfPage;

namespace PdfEditor.TextEdit.Tests;

// 0.4.0 V22/V23 탐침: 쪽 내용 해석 기반 글 찾기(글자 단위 위치) + 낱말 제자리 바꾸기. 정식 구현은 Task 4·5. 앱 코드가 아니라 시험 안에만 둔다.

internal sealed class PGlyph
{
    public string Text = "";
    public int Op, Str, Code;
    public double AdvText;                 // 글 공간 이동량(Tz 적용 전, Tc·Tw 포함)
    public double X0, Y0, X1, Y1;          // 쪽 공간 기준선 시작·끝(폭만, Tc 제외)
    public double Size;
    public int Angle;
    public bool Hidden, Virtual;
    public Mat Place;                      // 2x2 = 글 행렬×CTM, 이동 = 글자 시작점
    public double FontSize, Tz = 100, Tc, Ts;
    public string FontRes = "";
}

internal sealed class PFont
{
    public string Res = "";
    public bool Type0;
    public FontCodebook? Book;
    public double[]? Widths; public int First; public double Missing = 500;
    public bool Embedded;
    public bool Searchable => Book is not null || !Type0;
    public int Unit => Type0 ? 2 : 1;

    public IEnumerable<(int Code, string Text, double W)> Glyphs(byte[] bytes)
    {
        if (Type0)
        {
            if (Book is null) yield break;
            foreach (int c in Book.Codes(bytes))
            {
                string t = Book.Decode([(byte)(c >> 8), (byte)c]);
                double w = t.Length == 1 ? Book.Width1000(t) : 0;
                yield return (c, t, w <= 0 ? 1000 : w);
            }
            yield break;
        }
        foreach (byte b in bytes)
        {
            string t = Book is not null ? Book.Decode([b]) : ((char)b).ToString();
            double w = Widths is not null && b - First >= 0 && b - First < Widths.Length ? Widths[b - First] : Missing;
            yield return (b, t, w);
        }
    }
}

internal sealed record PMatch(IReadOnlyList<PGlyph> Glyphs, string Text, int LineIndex);

internal sealed class PLine
{
    public List<PGlyph> Glyphs = [];
    public string Text = "";
    public List<int> Map = [];   // 글자 위치 → Glyphs 번호
}

internal sealed class WalkResult
{
    public List<PGlyph> Glyphs = [];
    public int ShowOps, UnsearchableOps, QuoteOps;
    public Dictionary<string, PFont> Fonts = [];
    public ParsedContent? Pc;
    public string? Failure;
}

internal static class FindProbe
{
    public static WalkResult Walk(SharpPage page)
    {
        var res = new WalkResult();
        var loaded = PageContent.Load(page);
        if (loaded.Data is null) { res.Failure = loaded.Failure; return res; }
        var pc = ContentLexer.Parse(loaded.Data);
        res.Pc = pc;
        if (!pc.Ok) { res.Failure = pc.Failure; return res; }
        var fontsDict = page.Resources?.Elements.GetDictionary("/Font");
        // 1차: 글꼴별 보인 문자열
        var shown = new Dictionary<string, List<ReadOnlyMemory<byte>>>();
        string? cur = null;
        foreach (var op in pc.Ops)
        {
            if (op.Name == "Tf") cur = op.Operands.FirstOrDefault(o => o.Kind == OperandKind.Name)?.Text;
            else if (cur is not null && op.Name is "Tj" or "TJ" or "'" or "\"")
            {
                if (!shown.TryGetValue(cur, out var l)) shown[cur] = l = [];
                foreach (var o in op.Operands) CollectStrings(o, l);
            }
        }
        foreach (var (name, strs) in shown)
        {
            var fd = fontsDict?.Elements.GetDictionary("/" + name);
            if (fd is null) continue;
            var f = new PFont { Res = name, Type0 = fd.Elements.GetName("/Subtype") == "/Type0" };
            f.Book = FontCodebook.TryBuild(fd, strs);
            if (!f.Type0)
            {
                var w = fd.Elements.GetArray("/Widths");
                if (w is not null) { f.First = fd.Elements.ContainsKey("/FirstChar") ? fd.Elements.GetInteger("/FirstChar") : 0; f.Widths = w.Elements.Select(e => e is PdfInteger i ? (double)i.Value : e is PdfReal r ? r.Value : 500).ToArray(); }
                f.Embedded = fd.Elements.GetDictionary("/FontDescriptor") is { } d && (d.Elements.ContainsKey("/FontFile") || d.Elements.ContainsKey("/FontFile2") || d.Elements.ContainsKey("/FontFile3"));
            }
            res.Fonts[name] = f;
        }

        // 2차: 글 상태를 따라가며 글자 위치
        var stack = new Stack<Mat>();
        Mat ctm = Mat.Identity, tm = Mat.Identity, tlm = Mat.Identity;
        double tc = 0, tw = 0, tz = 100, tl = 0, ts = 0, fs = 0; int tr = 0;
        PFont? font = null;
        for (int i = 0; i < pc.Ops.Count; i++)
        {
            var op = pc.Ops[i];
            switch (op.Name)
            {
                case "q": stack.Push(ctm); break;
                case "Q": if (stack.Count > 0) ctm = stack.Pop(); break;
                case "cm": if (Nums(op, 6, out var m)) ctm = new Mat(m[0], m[1], m[2], m[3], m[4], m[5]).Then(ctm); break;
                case "BT": tm = tlm = Mat.Identity; break;
                case "Tc": if (Nums(op, 1, out var a1)) tc = a1[0]; break;
                case "Tw": if (Nums(op, 1, out var a2)) tw = a2[0]; break;
                case "Tz": if (Nums(op, 1, out var a3)) tz = a3[0]; break;
                case "TL": if (Nums(op, 1, out var a4)) tl = a4[0]; break;
                case "Ts": if (Nums(op, 1, out var a5)) ts = a5[0]; break;
                case "Tr": if (Nums(op, 1, out var a6)) tr = (int)a6[0]; break;
                case "Tf":
                {
                    string? n = op.Operands.FirstOrDefault(o => o.Kind == OperandKind.Name)?.Text;
                    font = n is not null && res.Fonts.TryGetValue(n, out var ff) ? ff : null;
                    fs = op.Operands.LastOrDefault(o => o.Kind == OperandKind.Number)?.Number ?? 0;
                    break;
                }
                case "Tm": if (Nums(op, 6, out var m6)) tm = tlm = new Mat(m6[0], m6[1], m6[2], m6[3], m6[4], m6[5]); break;
                case "Td": case "TD":
                    if (Nums(op, 2, out var t2)) { if (op.Name == "TD") tl = -t2[1]; tlm = Mat.Translate(t2[0], t2[1]).Then(tlm); tm = tlm; }
                    break;
                case "T*": tlm = Mat.Translate(0, -tl).Then(tlm); tm = tlm; break;
                case "Tj": case "TJ": case "'": case "\"":
                {
                    if (op.Name is "'" or "\"")
                    {
                        res.QuoteOps++;
                        if (op.Name == "\"" && op.Operands.Count >= 3 && op.Operands[0].Kind == OperandKind.Number && op.Operands[1].Kind == OperandKind.Number) { tw = op.Operands[0].Number; tc = op.Operands[1].Number; }
                        tlm = Mat.Translate(0, -tl).Then(tlm); tm = tlm;
                    }
                    res.ShowOps++;
                    var items = new List<Operand>();
                    if (op.Name == "TJ") { foreach (var o in op.Operands) if (o.Kind == OperandKind.Array) items.AddRange(o.Items); }
                    else if (op.Operands.Count > 0) items.Add(op.Operands[^1]);
                    if (font is null || !font.Searchable) { res.UnsearchableOps++; break; }   // 폭을 몰라 뒤 위치도 모른다 → 이 연산은 건너뜀
                    int strIdx = -1;
                    foreach (var it in items)
                    {
                        if (it.Kind == OperandKind.Number) { tm = Mat.Translate(-it.Number / 1000.0 * fs * tz / 100.0, 0).Then(tm); continue; }
                        if (it.Kind != OperandKind.String) continue;
                        strIdx++;
                        int codeIdx = 0;
                        foreach (var (code, text, w) in font.Glyphs(it.Bytes))
                        {
                            bool space = !font.Type0 && code == 32;
                            double adv = w / 1000.0 * fs + tc + (space ? tw : 0);
                            var full = tm.Then(ctm);
                            var (sx, sy) = full.Apply(0, 0);
                            var (ex, ey) = full.Apply(w / 1000.0 * fs * tz / 100.0, 0);
                            double ang = Math.Atan2(full.B, full.A) * 180 / Math.PI;
                            res.Glyphs.Add(new PGlyph
                            {
                                Text = text, Op = i, Str = strIdx, Code = codeIdx++, AdvText = adv,
                                X0 = sx, Y0 = sy, X1 = ex, Y1 = ey,
                                Size = fs * Math.Sqrt(full.C * full.C + full.D * full.D),
                                Angle = (int)Math.Round(((ang % 360) + 360) % 360),
                                Hidden = tr == 3,
                                Place = new Mat(full.A, full.B, full.C, full.D, sx, sy),
                                FontSize = fs, Tz = tz, Tc = tc, Ts = ts, FontRes = font.Res,
                            });
                            tm = Mat.Translate(adv * tz / 100.0, 0).Then(tm);
                        }
                    }
                    break;
                }
            }
        }
        return res;
    }

    private static void CollectStrings(Operand o, List<ReadOnlyMemory<byte>> into)
    {
        if (o.Kind == OperandKind.String) into.Add(o.Bytes);
        else if (o.Kind == OperandKind.Array) foreach (var x in o.Items) CollectStrings(x, into);
    }

    private static bool Nums(ContentOp op, int n, out double[] v)
    {
        v = new double[n];
        if (op.Operands.Count < n) return false;
        for (int k = 0; k < n; k++)
        {
            var o = op.Operands[op.Operands.Count - n + k];
            if (o.Kind != OperandKind.Number) return false;
            v[k] = o.Number;
        }
        return true;
    }

    // ------------------------------------------------------------------ 논리 글줄
    public static List<PLine> BuildLines(IReadOnlyList<PGlyph> glyphs)
    {
        var lines = new List<PLine>();
        foreach (var byAngle in glyphs.Where(g => !g.Hidden && g.Size > 0.01).GroupBy(g => g.Angle))
        {
            double th = byAngle.Key * Math.PI / 180, dx = Math.Cos(th), dy = Math.Sin(th), nx = -dy, ny = dx;
            var list = byAngle.Select(g => (G: g, V: g.X0 * nx + g.Y0 * ny, U0: g.X0 * dx + g.Y0 * dy, U1: g.X1 * dx + g.Y1 * dy)).OrderBy(t => t.V).ToList();
            for (int s = 0; s < list.Count;)
            {
                int e = s + 1;
                double meanV = list[s].V;
                while (e < list.Count && Math.Abs(list[e].V - meanV) <= 0.15 * list[s].G.Size) { meanV = (meanV * (e - s) + list[e].V) / (e - s + 1); e++; }
                var row = list.GetRange(s, e - s).OrderBy(t => t.U0).ToList();
                PLine? cur = null;
                double prevU1 = 0;
                foreach (var t in row)
                {
                    double gap = t.U0 - prevU1;
                    if (cur is null || gap > 1.0 * t.G.Size) { cur = new PLine(); lines.Add(cur); }
                    else if (gap > 0.25 * t.G.Size)
                    {
                        cur.Glyphs.Add(new PGlyph { Text = " ", Virtual = true, Op = -1 });
                        cur.Text += " "; cur.Map.Add(cur.Glyphs.Count - 1);
                    }
                    cur.Glyphs.Add(t.G);
                    foreach (char _ in t.G.Text) cur.Map.Add(cur.Glyphs.Count - 1);
                    cur.Text += t.G.Text;
                    prevU1 = Math.Max(t.U1, t.U0);
                }
                s = e;
            }
        }
        return lines;
    }

    // ------------------------------------------------------------------ 찾기
    public static List<PMatch> Find(IReadOnlyList<PLine> lines, string query, bool matchCase, bool wholeWord)
    {
        var res = new List<PMatch>();
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int li = 0; li < lines.Count; li++)
        {
            var ln = lines[li];
            for (int at = ln.Text.IndexOf(query, cmp); at >= 0; at = ln.Text.IndexOf(query, at + query.Length, cmp))
            {
                if (wholeWord && !WordBoundary(ln.Text, at, query.Length)) continue;
                var gs = new List<PGlyph>();
                for (int k = at; k < at + query.Length; k++)
                {
                    var g = ln.Glyphs[ln.Map[k]];
                    if (!g.Virtual && !gs.Contains(g)) gs.Add(g);
                }
                res.Add(new PMatch(gs, ln.Text.Substring(at, query.Length), li));
            }
        }
        return res;
    }

    private static bool WordBoundary(string t, int at, int len)
    {
        bool Word(int idx, int outward)
        {
            if (idx < 0 || idx >= t.Length) return false;
            char c = t[idx];
            if (char.IsLetterOrDigit(c) || c is '-' or '_') return true;
            if (c == '.') { int nb = idx + outward; return nb >= 0 && nb < t.Length && char.IsLetterOrDigit(t[nb]); }   // '.'은 앞뒤가 글자·숫자일 때만 낱말 글자(A.12)
            return false;
        }
        return !Word(at - 1, -1) && !Word(at + len, +1);
    }

    /// <summary>찾은 구간 → 글 연산 안 문자 범위로 되돌린 뒤 그 범위가 풀어낸 글이 찾은 글과 같은가.</summary>
    public static bool RangesDecodeBack(WalkResult w, PMatch m)
    {
        var sb = new StringBuilder();
        foreach (var g in m.Glyphs) sb.Append(g.Text);
        string joined = string.Concat(m.Text.Where(c => c != ' ' || m.Glyphs.Any(g => g.Text == " ")));
        return sb.ToString().Length > 0 && (sb.ToString() == m.Text || sb.ToString() == joined);
    }

    // ------------------------------------------------------------------ 바꾸기 (V23)
    public sealed record Region(double X0, double Y0, double X1, double Y1, double Size);
    public sealed record ReplaceOutcome(byte[]? Data, int Replaced, int NeedsAppFont, int Unsupported, string Notes, IReadOnlyList<Region>? Regions = null, int Overlaps = 0);

    /// <summary>
    /// 찾은 구간의 글 코드를 글 연산에서 빼고(그 폭만큼 TJ 간격으로 메움 → 뒤 글 위치 유지) 새 글을 첫 글자 자리에 같은 방향·크기로 덧붙인다.
    /// 새 글은 원래 글꼴 코드북(ToUnicode로 얻은 글자 → 보인 글 코드)으로 인코딩하고, 비임베드 단순 글꼴은 Latin-1로 직접 쓴다. 안 되면 NeedsAppFont.
    /// </summary>
    public static ReplaceOutcome Replace(WalkResult w, IReadOnlyList<PLine> lines, IReadOnlyList<PMatch> matches, string newText)
    {
        var pc = w.Pc!; var data = pc.Data;
        var removeByOp = new Dictionary<int, HashSet<(int Str, int Code)>>();
        var snippets = new List<byte[]>();
        int replaced = 0, needsApp = 0, unsupported = 0, overlaps = 0; var regions = new List<Region>();
        var advOf = w.Glyphs.ToDictionary(g => (g.Op, g.Str, g.Code), g => g.AdvText);
        foreach (var m in matches)
        {
            if (m.Glyphs.Any(g => pc.Ops[g.Op].Name is "'" or "\"")) { unsupported++; continue; }
            var first = m.Glyphs[0];
            var font = w.Fonts[first.FontRes];
            byte[]? bytes = null; bool twoByte = font.Type0;
            EncodedText? enc = null;
            if (font.Book is not null && font.Book.TryEncode(newText, out var e1)) { enc = e1; }
            else if (!font.Type0 && !font.Embedded && newText.All(c => c is >= ' ' and <= '~'))
            {
                bytes = Encoding.Latin1.GetBytes(newText);
                enc = new EncodedText(newText, bytes, [.. newText.Select(c => font.Widths is not null && c - font.First >= 0 && c - font.First < font.Widths.Length ? (float)font.Widths[c - font.First] : 500f)]);
            }
            if (enc is null) { needsApp++; continue; }
            foreach (var g in m.Glyphs)
            {
                if (!removeByOp.TryGetValue(g.Op, out var set)) removeByOp[g.Op] = set = [];
                set.Add((g.Str, g.Code));
            }
            var style = new TextLineStyle(SizePt: (float)first.FontSize, Tc: (float)first.Tc, Tz: (float)first.Tz, Ts: (float)first.Ts);
            snippets.Add(TextLineWriter.Write(twoByte, first.FontRes, enc, style, first.Place));
            replaced++;
            {   // 영역(시험 비교용)과 겹침 검사: 새 글이 같은 줄의 뒤 글을 덮는가
                double th = Math.Atan2(first.Place.B, first.Place.A), dx = Math.Cos(th), dy = Math.Sin(th);
                double sc = Math.Sqrt(first.Place.A * first.Place.A + first.Place.B * first.Place.B);   // 글 행렬의 가로 배율(Tm·CTM)
                double newLen = (enc.Widths.Sum() / 1000.0 * first.FontSize + first.Tc * enc.Widths.Length) * first.Tz / 100.0 * sc;
                var last = m.Glyphs[^1];
                double oldEndU = last.X1 * dx + last.Y1 * dy, startU = first.X0 * dx + first.Y0 * dy, newEndU = startU + newLen;
                regions.Add(new Region(first.X0, first.Y0, first.X0 + dx * Math.Max(newLen, oldEndU - startU), first.Y0 + dy * Math.Max(newLen, oldEndU - startU), first.Size));
                if (lines[m.LineIndex].Glyphs.Any(g => !g.Virtual && !m.Glyphs.Contains(g) && g.X0 * dx + g.Y0 * dy >= oldEndU - 0.01 && g.X0 * dx + g.Y0 * dy < newEndU - 0.01)) overlaps++;
            }
        }
        if (replaced == 0) return new ReplaceOutcome(null, 0, needsApp, unsupported, "nothing replaced");

        var edits = new List<(int Start, int End, byte[] Text)>();
        foreach (var (opIdx, set) in removeByOp)
        {
            var op = pc.Ops[opIdx];
            var font = w.Fonts[w.Glyphs.First(g => g.Op == opIdx).FontRes];
            double fs = w.Glyphs.First(g => g.Op == opIdx).FontSize;
            var sb = new StringBuilder("[");
            var items = new List<Operand>();
            if (op.Name == "TJ") { foreach (var o in op.Operands) if (o.Kind == OperandKind.Array) items.AddRange(o.Items); }
            else items.Add(op.Operands[^1]);
            int str = -1;
            foreach (var it in items)
            {
                if (it.Kind == OperandKind.Number) { sb.Append(Mat_F(it.Number)).Append(' '); continue; }
                if (it.Kind != OperandKind.String) continue;
                str++;
                int unit = font.Unit;
                int n = it.Bytes.Length / unit;
                int runStart = 0; double removedAdv = 0; bool inRemoved = false;
                void FlushKept(int from, int to) { if (to > from) sb.Append('<').Append(Convert.ToHexString(it.Bytes, from * unit, (to - from) * unit)).Append("> "); }
                for (int c = 0; c < n; c++)
                {
                    bool rem = set.Contains((str, c));
                    if (rem && !inRemoved) { FlushKept(runStart, c); inRemoved = true; removedAdv = 0; }
                    if (!rem && inRemoved) { sb.Append(Mat_F(-removedAdv * 1000.0 / Math.Max(fs, 1e-6))).Append(' '); inRemoved = false; runStart = c; }
                    if (rem) removedAdv += advOf[(opIdx, str, c)];
                }
                if (inRemoved) sb.Append(Mat_F(-removedAdv * 1000.0 / Math.Max(fs, 1e-6))).Append(' ');
                else FlushKept(runStart, n);
            }
            sb.Append("] TJ");
            edits.Add((op.Start, op.End, Encoding.Latin1.GetBytes(sb.ToString())));
        }
        var ms = new MemoryStream();
        int pos = 0;
        foreach (var (s, e, t) in edits.OrderBy(x => x.Start)) { ms.Write(data, pos, s - pos); ms.Write(t); pos = e; }
        ms.Write(data, pos, data.Length - pos);
        var spliced = ContentLexer.Parse(ms.ToArray());
        if (!spliced.Ok) return new ReplaceOutcome(null, 0, needsApp, unsupported, "splice does not parse: " + spliced.Failure);
        var res = ContentRewriter.AppendOnly(spliced, snippets);
        return res.Ok ? new ReplaceOutcome(res.Data, replaced, needsApp, unsupported, "", regions, overlaps) : new ReplaceOutcome(null, 0, needsApp, unsupported, "append: " + res.Failure);
    }

    private static string Mat_F(double v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
