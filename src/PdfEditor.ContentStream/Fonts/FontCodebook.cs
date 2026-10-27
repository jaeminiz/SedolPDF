using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.ContentStream.Fonts;

/// <summary>인코딩 결과: 글 코드 바이트와 글자(UTF-16 단위)마다의 폭(1000 단위). <see cref="Widths"/>의 길이는 <see cref="Text"/>와 같다.</summary>
public sealed record EncodedText(string Text, byte[] Bytes, ImmutableArray<float> Widths)
{
    public float Total1000 => Widths.Sum();
}

/// <summary>
/// 원래 글꼴로 새 글을 직접 인코딩하기 위한 코드북(M5 C8). 글꼴 사전에서 ToUnicode(코드 → 글자)와 폭을 읽고,
/// <b>이 문단에서 실제로 보인 글 코드</b>의 (글자 → 글 코드, 폭)만 담는다 — 보였던 코드는 서브셋에 글리프가 있다는 보증이 구조적으로 생긴다.
/// 만들 수 없으면(ToUnicode 없음·폭 없음·Type3·혼합 코드 공간·비임베드·글자 충돌) <c>null</c>. PDFsharp만 쓰고 PDFium은 쓰지 않는다.
/// </summary>
public sealed class FontCodebook
{
    private readonly Dictionary<char, int> _charToCode = [];
    private readonly Dictionary<int, string> _codeToText = [];
    private readonly Dictionary<int, double> _widths = [];
    private readonly double _defaultWidth;

    /// <summary>2바이트(Type0 Identity-H) 코드인가. 아니면 1바이트.</summary>
    public bool TwoByte { get; }

    /// <summary>이 코드북으로 쓸 수 있는 글자(문단에서 실제로 보인 글자).</summary>
    public IReadOnlyCollection<char> Available => _charToCode.Keys;

    private FontCodebook(bool twoByte, double defaultWidth) { TwoByte = twoByte; _defaultWidth = defaultWidth; }

    /// <summary>엄격 규칙(ToUnicode + 폭 필요).</summary>
    public static FontCodebook? TryBuild(PdfDictionary fontDict, IEnumerable<ReadOnlyMemory<byte>> shownStrings) =>
        TryBuild(fontDict, shownStrings, null, out _);

    /// <summary>
    /// <paramref name="learnFrom"/>이 있으면 ToUnicode가 없는 글꼴도 (글 코드열, PDFium이 읽은 글) 쌍으로 코드 → 글자를 배운다(C8 확장).
    /// 쌍은 코드 수 = 글자 수일 때만(공백까지 1:1, 아니면 공백을 뺀 글자 수와 코드 수가 같을 때 비공백만), 같은 코드가 다른 글자로 풀리면 그 글꼴은 제외.
    /// </summary>
    public static FontCodebook? TryBuild(PdfDictionary fontDict, IEnumerable<ReadOnlyMemory<byte>> shownStrings,
        IReadOnlyList<(ReadOnlyMemory<byte> Bytes, string Text)>? learnFrom, out string? reason)
    {
        reason = null;
        string subtype = fontDict.Elements.GetName("/Subtype") ?? "";
        if (subtype == "/Type3") { reason = "type3"; return null; }

        bool twoByte = subtype == "/Type0";
        PdfDictionary? widthSource = fontDict;
        PdfDictionary? descriptorOwner = fontDict;
        if (twoByte)
        {
            string? enc = fontDict.Elements.GetName("/Encoding");
            if (enc != "/Identity-H") { reason = "mixed-code-space"; return null; }
            widthSource = descriptorOwner = Descendant(fontDict);
            if (widthSource is null) { reason = "no-descendant"; return null; }
        }
        else if (subtype is not ("/TrueType" or "/Type1" or "/MMType1")) { reason = "unsupported-subtype"; return null; }

        if (!IsEmbedded(descriptorOwner)) { reason = "not-embedded"; return null; }

        FontCodebook book;
        if (twoByte)
        {
            book = new FontCodebook(true, widthSource!.Elements.ContainsKey("/DW") ? Real(widthSource.Elements["/DW"]) : 1000);
            ParseW(widthSource.Elements.GetArray("/W"), book);
        }
        else
        {
            var widths = fontDict.Elements.GetArray("/Widths");
            if (widths is null) { reason = "no-widths"; return null; }
            var fd = fontDict.Elements.GetDictionary("/FontDescriptor");
            double missing = fd is not null && fd.Elements.ContainsKey("/MissingWidth") ? Real(fd.Elements["/MissingWidth"]) : 0;
            book = new FontCodebook(false, missing);
            int first = fontDict.Elements.ContainsKey("/FirstChar") ? fontDict.Elements.GetInteger("/FirstChar") : 0;
            for (int i = 0; i < widths.Elements.Count; i++) book._widths[first + i] = Real(widths.Elements[i]);
        }

        var codeText = new Dictionary<int, string>();
        bool hasToUnicode = ParseToUnicode(fontDict, codeText);
        if (!hasToUnicode)
        {
            if (learnFrom is null) { reason = "no-tounicode"; return null; }
            if (!Learn(book, learnFrom, codeText)) { reason = "learn-conflict"; return null; }
            if (codeText.Count == 0) { reason = "no-tounicode"; return null; }
        }

        // 이 문단에서 실제로 보인 코드만 코드북에 넣는다.
        foreach (var s in shownStrings)
        {
            foreach (int code in book.Codes(s.Span))
            {
                if (!codeText.TryGetValue(code, out var text)) continue;
                book._codeToText[code] = text;
                if (!book._widths.ContainsKey(code))
                {
                    if (!twoByte && book._defaultWidth <= 0) { reason = "width-missing"; return null; }
                }
                if (text.Length == 1) book._charToCode.TryAdd(text[0], code);
            }
        }
        if (book._charToCode.Count == 0) { reason = "no-usable-chars"; return null; }
        return book;
    }

    /// <summary>글자 폭(1000 단위). 코드북에 없는 글자는 0.</summary>
    public float Width1000(string ch) => ch.Length == 1 && _charToCode.TryGetValue(ch[0], out int code) ? (float)WidthOf(code) : 0;

    public float Width1000(char ch) => _charToCode.TryGetValue(ch, out int code) ? (float)WidthOf(code) : 0;

    private double WidthOf(int code) => _widths.TryGetValue(code, out var w) ? w : _defaultWidth;

    /// <summary>글 코드의 폭(1000 단위, 폭 표에 없으면 기본 폭). 찾기 엔진이 글자 위치를 계산할 때 쓴다.</summary>
    public double WidthOfCode(int code) => WidthOf(code);

    /// <summary>글 코드가 풀리는 글자(코드북이 아는 코드만, 모르면 null).</summary>
    public string? TextOfCode(int code) => _codeToText.TryGetValue(code, out var t) ? t : null;

    /// <summary>모든 글자가 코드북에 있으면 인코딩한다.</summary>
    public bool TryEncode(string text, out EncodedText enc)
    {
        var bytes = new List<byte>(text.Length * (TwoByte ? 2 : 1));
        var widths = ImmutableArray.CreateBuilder<float>(text.Length);
        foreach (char ch in text)
        {
            if (!_charToCode.TryGetValue(ch, out int code)) { enc = null!; return false; }
            if (TwoByte) { bytes.Add((byte)(code >> 8)); bytes.Add((byte)code); }
            else bytes.Add((byte)code);
            widths.Add((float)WidthOf(code));
        }
        enc = new EncodedText(text, bytes.ToArray(), widths.MoveToImmutable());
        return true;
    }

    /// <summary>문자열 바이트를 글자로 푼다(모르는 코드는 U+FFFD).</summary>
    public string Decode(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        foreach (int code in Codes(bytes)) sb.Append(_codeToText.TryGetValue(code, out var t) ? t : "�");
        return sb.ToString();
    }

    /// <summary>바이트열 → 글 코드열(2바이트 글꼴은 큰 바이트가 먼저, 홀수 바이트의 끝 한 개는 버림).</summary>
    public IEnumerable<int> Codes(ReadOnlySpan<byte> bytes) => CodesOf(bytes, TwoByte);

    private static List<int> CodesOf(ReadOnlySpan<byte> b, bool twoByte)
    {
        var l = new List<int>(twoByte ? b.Length / 2 : b.Length);
        if (twoByte) { for (int i = 0; i + 1 < b.Length; i += 2) l.Add(b[i] << 8 | b[i + 1]); }
        else foreach (var x in b) l.Add(x);
        return l;
    }

    // ------------------------------------------------------------------ 글꼴 사전 읽기
    private static PdfDictionary? Descendant(PdfDictionary f)
    {
        var arr = f.Elements.GetArray("/DescendantFonts");
        if (arr is null || arr.Elements.Count == 0) return null;
        var it = arr.Elements[0];
        return (it is PdfReference r ? r.Value : it) as PdfDictionary;
    }

    private static bool IsEmbedded(PdfDictionary? f)
    {
        var fd = f?.Elements.GetDictionary("/FontDescriptor");
        return fd is not null && (fd.Elements.ContainsKey("/FontFile") || fd.Elements.ContainsKey("/FontFile2") || fd.Elements.ContainsKey("/FontFile3"));
    }

    private static double Real(PdfItem? it)
    {
        if (it is PdfReference r) it = r.Value;
        return it switch { PdfInteger i => i.Value, PdfReal d => d.Value, PdfLongInteger l => l.Value, _ => 0 };
    }

    private static void ParseW(PdfArray? w, FontCodebook b)
    {
        if (w is null) return;
        var e = w.Elements;
        for (int i = 0; i < e.Count;)
        {
            int c = (int)Real(e[i]);
            if (i + 1 >= e.Count) break;
            var n1 = e[i + 1] is PdfReference r1 ? r1.Value : e[i + 1];
            if (n1 is PdfArray arr)
            {
                for (int k = 0; k < arr.Elements.Count; k++) b._widths[c + k] = Real(arr.Elements[k]);
                i += 2;
            }
            else
            {
                if (i + 2 >= e.Count) break;
                int c2 = (int)Real(n1);
                double wv = Real(e[i + 2]);
                for (int k = c; k <= c2 && k - c < 70000; k++) b._widths[k] = wv;
                i += 3;
            }
        }
    }

    private static readonly Regex BfChar = new(@"beginbfchar(.*?)endbfchar", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BfRange = new(@"beginbfrange(.*?)endbfrange", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Hex = new(@"<([0-9A-Fa-f\s]*)>", RegexOptions.Compiled);

    private static bool ParseToUnicode(PdfDictionary f, Dictionary<int, string> map)
    {
        var tu = f.Elements.GetDictionary("/ToUnicode");
        if (tu?.Stream is null) return false;
        string s;
        try { s = Encoding.Latin1.GetString(tu.Stream.UnfilteredValue); }
        catch { return false; }
        foreach (Match m in BfChar.Matches(s))
        {
            var h = Hex.Matches(m.Groups[1].Value);
            for (int i = 0; i + 1 < h.Count; i += 2) map[HexInt(h[i].Groups[1].Value)] = HexStr(h[i + 1].Groups[1].Value);
        }
        foreach (Match m in BfRange.Matches(s))
        {
            foreach (var line in m.Groups[1].Value.Split('\n', '\r'))
            {
                var h = Hex.Matches(line);
                if (h.Count < 3) continue;
                int lo = HexInt(h[0].Groups[1].Value), hi = HexInt(h[1].Groups[1].Value);
                if (line.Contains('['))
                {
                    for (int k = 0; k < h.Count - 2 && lo + k <= hi; k++) map[lo + k] = HexStr(h[2 + k].Groups[1].Value);
                }
                else
                {
                    string dst = HexStr(h[2].Groups[1].Value);
                    for (int k = 0; lo + k <= hi && k < 70000; k++)
                        map[lo + k] = dst.Length == 0 ? "" : dst[..^1] + (char)(dst[^1] + k);
                }
            }
        }
        return map.Count > 0;
    }

    private static int HexInt(string h) => int.Parse(Compact(h), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string Compact(string h) => h.Replace(" ", "").Replace("\r", "").Replace("\n", "").Replace("\t", "");

    private static string HexStr(string h)
    {
        h = Compact(h);
        var sb = new StringBuilder();
        if (h.Length == 2) { sb.Append((char)int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)); return sb.ToString(); }
        for (int i = 0; i + 4 <= h.Length; i += 4) sb.Append((char)int.Parse(h.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static bool Learn(FontCodebook book, IReadOnlyList<(ReadOnlyMemory<byte> Bytes, string Text)> pairs, Dictionary<int, string> map)
    {
        foreach (var (bytes, text) in pairs)
        {
            var codes = CodesOf(bytes.Span, book.TwoByte);
            string? chars = null;
            if (codes.Count == text.Length) chars = text;
            else
            {
                string stripped = new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
                if (codes.Count > 0 && stripped.Length == codes.Count(c => !IsSpaceCode(book, c))) chars = stripped;
                if (chars is not null)
                {
                    // 공백 코드는 건너뛰고 비공백 코드와 글자를 1:1
                    int ci = 0;
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (IsSpaceCode(book, codes[i])) continue;
                        if (!Put(map, codes[i], chars[ci++].ToString())) return false;
                    }
                    continue;
                }
            }
            if (chars is null) continue;                                    // 짝이 안 맞는 줄은 배우지 않는다
            for (int i = 0; i < codes.Count; i++)
                if (!Put(map, codes[i], chars[i].ToString())) return false;
        }
        return true;
    }

    // 짝이 안 맞아 공백을 뺀 경우: 1바이트 글꼴의 32번만 공백 코드로 본다(2바이트는 공백 코드를 알 수 없음).
    private static bool IsSpaceCode(FontCodebook book, int code) => !book.TwoByte && code == 32;

    private static bool Put(Dictionary<int, string> map, int code, string s)
    {
        if (map.TryGetValue(code, out var old)) return old == s;
        map[code] = s;
        return true;
    }
}
