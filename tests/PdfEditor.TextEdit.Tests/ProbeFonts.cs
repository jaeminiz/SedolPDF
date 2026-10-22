using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfEditor.ContentStream;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using SharpPage = PdfSharp.Pdf.PdfPage;

namespace PdfEditor.TextEdit.Tests;

/// <summary>탐침용 코드북: 글꼴 사전 → (코드 → 글자, 코드 → 폭). 정식 FontCodebook(Task 3)의 규칙(C8)을 그대로 따른다.</summary>
internal sealed class Book
{
    public string Kind = "";
    public bool TwoByte;
    public Dictionary<int, string> ToUni = new();
    public Dictionary<int, double> Widths = new();
    public double Dw = 1000, Missing;
    public bool Relaxed;                          // ToUnicode 대신 WinAnsi(Latin-1)로 푼 경우
    public Dictionary<char, int> CharToCode = new();

    public double Width(int code) => Widths.TryGetValue(code, out var w) ? w : TwoByte ? Dw : Missing;

    public IEnumerable<int> Codes(byte[] b)
    {
        if (TwoByte) { for (int i = 0; i + 1 < b.Length; i += 2) yield return b[i] << 8 | b[i + 1]; }
        else foreach (var x in b) yield return x;
    }

    public string Decode(byte[] bytes)
    {
        var sb = new StringBuilder();
        foreach (int c in Codes(bytes))
        {
            if (ToUni.TryGetValue(c, out var s)) sb.Append(s);
            else if (Relaxed && c is >= 32 and < 256) sb.Append((char)c);
            else sb.Append('�');
        }
        return sb.ToString();
    }

    public void BuildInverse()
    {
        foreach (var (code, s) in ToUni) if (s.Length == 1 && !CharToCode.ContainsKey(s[0])) CharToCode[s[0]] = code;
        if (Relaxed) for (int c = 32; c < 256; c++) CharToCode.TryAdd((char)c, c);
    }
}

internal static class BookBuilder
{
    /// <summary>글꼴 종류 이름(표 집계용).</summary>
    public static string KindOf(PdfDictionary f)
    {
        string st = f.Elements.GetName("/Subtype") ?? "?";
        if (st == "/Type0")
        {
            var d = Descendant(f);
            string ds = d?.Elements.GetName("/Subtype") ?? "?";
            return $"Type0/{ds.TrimStart('/')}/{(Embedded(d) is { } e ? e : "noemb")}";
        }
        return $"{st.TrimStart('/')}/{(Embedded(f) is { } e2 ? e2 : "noemb")}";
    }

    private static string? Embedded(PdfDictionary? f)
    {
        var fd = f?.Elements.GetDictionary("/FontDescriptor");
        if (fd is null) return null;
        if (fd.Elements.ContainsKey("/FontFile2")) return "FF2(TrueType)";
        if (fd.Elements.ContainsKey("/FontFile3")) return "FF3(CFF/OpenType)";
        if (fd.Elements.ContainsKey("/FontFile")) return "FF(Type1)";
        return null;
    }

    private static PdfDictionary? Descendant(PdfDictionary f)
    {
        var arr = f.Elements.GetArray("/DescendantFonts");
        if (arr is null || arr.Elements.Count == 0) return null;
        var it = arr.Elements[0];
        return (it is PdfReference r ? r.Value : it) as PdfDictionary;
    }

    /// <summary>만들 수 없으면 Book = null과 이유. relaxed면 ToUnicode가 없어도 WinAnsi 단순 글꼴은 허용한다(정식 규칙 밖 — 참고 수치용).</summary>
    public static (Book? Book, string Reason) Build(PdfDictionary f, bool relaxed = false, bool noToUni = false)
    {
        string st = f.Elements.GetName("/Subtype") ?? "?";
        var b = new Book { Kind = KindOf(f) };
        if (st == "/Type3") return (null, "Type3");
        bool hasToUni = TryToUnicode(f, b);
        if (st == "/Type0")
        {
            var enc = f.Elements.GetName("/Encoding");
            if (enc is not ("/Identity-H" or "/Identity-V")) return (null, "Type0 CMap " + (enc ?? "(stream)") + " (mixed code space)");
            b.TwoByte = true;
            if (!hasToUni && !noToUni) return (null, "no ToUnicode");
            var d = Descendant(f);
            if (d is null) return (null, "no descendant");
            b.Dw = d.Elements.ContainsKey("/DW") ? d.Elements.GetReal("/DW") : 1000;
            ParseW(d.Elements.GetArray("/W"), b);
            b.BuildInverse();
            return (b, "");
        }
        // 단순 글꼴
        var widths = f.Elements.GetArray("/Widths");
        if (widths is null) return (null, "no Widths (standard 14)");
        int first = f.Elements.ContainsKey("/FirstChar") ? f.Elements.GetInteger("/FirstChar") : 0;
        for (int i = 0; i < widths.Elements.Count; i++) b.Widths[first + i] = Num(widths.Elements[i]);
        var fd = f.Elements.GetDictionary("/FontDescriptor");
        b.Missing = fd is not null && fd.Elements.ContainsKey("/MissingWidth") ? fd.Elements.GetReal("/MissingWidth") : 0;
        if (!hasToUni && !noToUni)
        {
            if (!relaxed) return (null, "no ToUnicode");
            b.Relaxed = true;
        }
        b.BuildInverse();
        return (b, "");
    }

    private static double Num(PdfItem it)
    {
        if (it is PdfReference r) it = r.Value;
        return it switch { PdfInteger i => i.Value, PdfReal d => d.Value, PdfLongInteger l => l.Value, _ => 0 };
    }

    private static void ParseW(PdfArray? w, Book b)
    {
        if (w is null) return;
        var e = w.Elements;
        for (int i = 0; i < e.Count;)
        {
            var a = e[i] is PdfReference r0 ? r0.Value : e[i];
            int c = (int)Num(a);
            if (i + 1 >= e.Count) break;
            var n1 = e[i + 1] is PdfReference r1 ? r1.Value : e[i + 1];
            if (n1 is PdfArray arr)
            {
                for (int k = 0; k < arr.Elements.Count; k++) b.Widths[c + k] = Num(arr.Elements[k]);
                i += 2;
            }
            else
            {
                if (i + 2 >= e.Count) break;
                int c2 = (int)Num(n1); double wv = Num(e[i + 2]);
                for (int k = c; k <= c2 && k - c < 70000; k++) b.Widths[k] = wv;
                i += 3;
            }
        }
    }

    private static readonly Regex BfChar = new(@"beginbfchar(.*?)endbfchar", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BfRange = new(@"beginbfrange(.*?)endbfrange", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Hex = new(@"<([0-9A-Fa-f\s]*)>", RegexOptions.Compiled);

    private static bool TryToUnicode(PdfDictionary f, Book b)
    {
        var tu = f.Elements.GetDictionary("/ToUnicode");
        if (tu?.Stream is null) return false;
        string s = Encoding.Latin1.GetString(tu.Stream.UnfilteredValue);
        foreach (Match m in BfChar.Matches(s))
        {
            var h = Hex.Matches(m.Groups[1].Value);
            for (int i = 0; i + 1 < h.Count; i += 2) b.ToUni[HexInt(h[i].Groups[1].Value)] = HexStr(h[i + 1].Groups[1].Value);
        }
        foreach (Match m in BfRange.Matches(s))
        {
            // 한 줄: <lo> <hi> <dst>  또는  <lo> <hi> [<d1> <d2> ...]
            foreach (var line in m.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var h = Hex.Matches(line);
                if (h.Count < 3) continue;
                int lo = HexInt(h[0].Groups[1].Value), hi = HexInt(h[1].Groups[1].Value);
                if (line.Contains('['))
                    for (int k = 0; k < h.Count - 2 && lo + k <= hi; k++) b.ToUni[lo + k] = HexStr(h[2 + k].Groups[1].Value);
                else
                {
                    string dst = HexStr(h[2].Groups[1].Value);
                    for (int k = 0; lo + k <= hi && k < 70000; k++)
                        b.ToUni[lo + k] = dst.Length == 0 ? "" : dst[..^1] + (char)(dst[^1] + k);
                }
            }
        }
        return b.ToUni.Count > 0;
    }

    private static int HexInt(string h) => int.Parse(h.Replace(" ", "").Replace("\r", "").Replace("\n", ""), NumberStyles.HexNumber);

    private static string HexStr(string h)
    {
        h = h.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        var sb = new StringBuilder();
        for (int i = 0; i + 3 < h.Length + 0 && i + 4 <= h.Length; i += 4) sb.Append((char)int.Parse(h.AsSpan(i, 4), NumberStyles.HexNumber));
        if (h.Length is 2) sb.Append((char)int.Parse(h, NumberStyles.HexNumber));
        return sb.ToString();
    }
}

/// <summary>PDFium 글 객체 번호 → 내용 스트림 안의 그 객체(글꼴 사전·글 상태·보인 문자열). V16 규칙으로 번호가 맞는 쪽만.</summary>
internal sealed class TextStream
{
    public PdfDictionary Font = null!;
    public string FontRes = "";
    public double Tf, Tc, Tw, Tz;
    public List<(byte[]? Bytes, double Adj)> Pieces = [];
    public (double X, double Y)? Origin;
}

internal static class StreamMap
{
    internal static string Key(IEnumerable<ObjectKind> kinds) => string.Concat(kinds.Select(k => (char)('0' + (int)k)));

    /// <summary>해석기 객체 종류 열이 PDFium과 같으면 글 객체별 스트림 정보를, 아니면 null(+이유).</summary>
    public static Dictionary<int, TextStream>? Build(SharpPage page, IReadOnlyList<ObjectKind> pdfiumKinds, out string? failure, out double ms, out int objCount)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        failure = null; objCount = 0;
        var loaded = PageContent.Load(page);
        if (loaded.Data is null) { failure = loaded.Failure; ms = sw.Elapsed.TotalMilliseconds; return null; }
        var pc = ContentLexer.Parse(loaded.Data);
        var seg = ContentSegmenter.Segment(pc, PageContent.XObjectResolver(page));
        ms = sw.Elapsed.TotalMilliseconds;
        if (!seg.Ok) { failure = seg.Failure; return null; }
        objCount = seg.Objects.Count;
        if (seg.Objects.Count != pdfiumKinds.Count || Key(seg.Objects.Select(o => o.Kind)) != Key(pdfiumKinds))
        { failure = $"object sequence differs (ours {seg.Objects.Count}, pdfium {pdfiumKinds.Count})"; return null; }
        var fonts = page.Resources?.Elements.GetDictionary("/Font");
        var map = new Dictionary<int, TextStream>();
        for (int i = 0; i < seg.Objects.Count; i++)
        {
            var o = seg.Objects[i];
            if (o.Kind != ObjectKind.Text || o.Text is not { } snap || snap.FontOp < 0) continue;
            var tf = pc.Ops[snap.FontOp];
            string? name = tf.Operands.FirstOrDefault(x => x.Kind == OperandKind.Name)?.Text;
            if (name is null || fonts is null) continue;
            var fd = fonts.Elements.GetDictionary("/" + name);
            if (fd is null) continue;
            var ts = new TextStream { Font = fd, FontRes = name, Tc = snap.Tc, Tw = snap.Tw, Tz = snap.Tz, Origin = o.Origin };
            var sizeOp = tf.Operands.LastOrDefault(x => x.Kind == OperandKind.Number);
            ts.Tf = sizeOp?.Number ?? 0;
            var show = pc.Ops[o.OpIndex];
            void Add(Operand x)
            {
                if (x.Kind == OperandKind.String) ts.Pieces.Add((x.Bytes, 0));
                else if (x.Kind == OperandKind.Number) ts.Pieces.Add((null, x.Number));
                else if (x.Kind == OperandKind.Array) foreach (var y in x.Items) Add(y);
            }
            if (show.Name is "Tj" or "TJ") foreach (var x in show.Operands) Add(x);
            else if (show.Operands.Count > 0) Add(show.Operands[^1]);
            map[i] = ts;
        }
        return map;
    }

    /// <summary>글 상태(자간·낱말 간격·가로 비율·TJ 조정)까지 반영한 글자 방향 이동량(쪽 단위 = Tf 단위 × scale).</summary>
    public static double Advance(Book book, TextStream ts, double scale)
    {
        double adv = 0;
        foreach (var (bytes, adj) in ts.Pieces)
        {
            if (bytes is null) { adv -= adj / 1000.0 * ts.Tf; continue; }
            foreach (int code in book.Codes(bytes))
            {
                adv += book.Width(code) / 1000.0 * ts.Tf + ts.Tc;
                if (!book.TwoByte && code == 32) adv += ts.Tw;
            }
        }
        return adv * ts.Tz / 100.0 * scale;
    }
}
