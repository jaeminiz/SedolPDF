using System.Text;

namespace PdfEditor.ContentStream;

public enum OperandKind { Number, Name, String, Array, Dict, Bool, Null }

/// <summary>연산자 앞 피연산자 하나. Start/End는 최상위 피연산자일 때 원본 바이트 범위.</summary>
public sealed class Operand
{
    public OperandKind Kind { get; init; }
    public double Number { get; init; }
    /// <summary>이름(# 해독 후, 앞의 / 없음).</summary>
    public string Text { get; init; } = "";
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    /// <summary>배열 원소, 또는 딕셔너리의 (키, 값, 키, 값…) 평면 목록.</summary>
    public List<Operand> Items { get; init; } = SharedEmpty;
    private static readonly List<Operand> SharedEmpty = [];   // 초기화 때만 바꾸고 나중에 Add하지 않으므로 공유해도 안전(토큰마다 목록을 만들던 비용 제거)
    public bool Bool { get; init; }
    public int Start { get; internal set; }
    public int End { get; internal set; }
}

/// <summary>
/// 연산자 하나와 그 피연산자. [<see cref="Start"/>, <see cref="End"/>)는 첫 피연산자(없으면 연산자)부터 연산자 끝까지의 원본 바이트 범위.
/// 인라인 이미지는 `BI … ID data EI` 전체가 연산자 "BI" 하나다.
/// </summary>
public sealed class ContentOp
{
    public string Name { get; init; } = "";
    public int Start { get; init; }
    /// <summary>연산자 이름 자체의 시작.</summary>
    public int OpStart { get; init; }
    public int End { get; init; }
    public IReadOnlyList<Operand> Operands { get; init; } = [];
    /// <summary>인라인 이미지 데이터 범위(없으면 -1).</summary>
    public int DataStart { get; init; } = -1;
    public int DataEnd { get; init; } = -1;
}

/// <summary>어휘 분석 결과. 해석 불가면 <see cref="Failure"/>가 채워지고 <see cref="Ops"/>는 실패 지점 앞까지만 든다(예외를 던지지 않는다).</summary>
public sealed class ParsedContent
{
    public required byte[] Data { get; init; }
    public required List<ContentOp> Ops { get; init; }
    public string? Failure { get; init; }
    public bool Ok => Failure is null;
}

public static class ContentLexer
{
    private const int MaxDepth = 32;
    private const int MaxTopOperands = 20000;
    private const long MaxOps = 100_000_000;

    private static bool IsWs(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelim(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
    private static bool IsRegular(byte b) => !IsWs(b) && !IsDelim(b);

    public static ParsedContent Parse(byte[] data)
    {
        var ops = new List<ContentOp>();
        var operands = new List<Operand>();
        int pos = 0, opStart = -1;
        string? failure = null;
        try
        {
            while (true)
            {
                SkipWsAndComments(data, ref pos);
                if (pos >= data.Length) break;
                int tokStart = pos;
                var t = ReadToken(data, ref pos, 0);
                if (t.Error is not null) { failure = t.Error + " @" + tokStart; break; }
                if (t.Operand is not null)
                {
                    if (operands.Count == 0) opStart = tokStart;
                    if (operands.Count >= MaxTopOperands) { failure = "too many operands @" + tokStart; break; }
                    t.Operand.Start = tokStart; t.Operand.End = pos;
                    operands.Add(t.Operand);
                    continue;
                }
                string kw = t.Keyword!;
                int start = operands.Count == 0 ? tokStart : opStart;
                if (ops.Count >= MaxOps) { failure = "too many operators"; break; }
                if (kw == "BI")
                {
                    var (op, err) = ReadInlineImage(data, ref pos, start, tokStart);
                    if (err is not null) { failure = err + " @" + tokStart; break; }
                    ops.Add(op!);
                }
                else
                {
                    ops.Add(new ContentOp { Name = kw, Start = start, OpStart = tokStart, End = pos, Operands = operands.ToArray() });
                }
                operands.Clear(); opStart = -1;
            }
        }
        catch (Exception e) { failure = "lexer:" + e.GetType().Name; }
        // 키워드 없이 끝난 피연산자(스트림 끝에 남은 숫자 등)는 연산자가 아니므로 무시하되, 원본 바이트는 그대로 유지된다.
        return new ParsedContent { Data = data, Ops = ops, Failure = failure };
    }

    private static void SkipWsAndComments(byte[] d, ref int p)
    {
        while (p < d.Length)
        {
            byte b = d[p];
            if (IsWs(b)) p++;
            else if (b == '%') { while (p < d.Length && d[p] != 10 && d[p] != 13) p++; }
            else break;
        }
    }

    private readonly record struct Token(Operand? Operand, string? Keyword, string? Error);

    private static Token Tok(Operand o) => new(o, null, null);
    private static Token Err(string e) => new(null, null, e);

    private static Token ReadToken(byte[] d, ref int p, int depth)
    {
        if (depth > MaxDepth) return Err("nesting too deep");
        byte c = d[p];
        switch (c)
        {
            case (byte)'/':
            {
                p++;
                var sb = new StringBuilder();
                while (p < d.Length && IsRegular(d[p]))
                {
                    if (d[p] == '#' && p + 2 < d.Length && Hex(d[p + 1]) >= 0 && Hex(d[p + 2]) >= 0)
                    { sb.Append((char)(Hex(d[p + 1]) * 16 + Hex(d[p + 2]))); p += 3; }
                    else sb.Append((char)d[p++]);
                }
                return Tok(new Operand { Kind = OperandKind.Name, Text = sb.ToString() });
            }
            case (byte)'(':
                return ReadLiteralString(d, ref p);
            case (byte)'<':
                if (p + 1 < d.Length && d[p + 1] == '<') return ReadDict(d, ref p, depth);
                return ReadHexString(d, ref p);
            case (byte)'[':
            {
                p++;
                var items = new List<Operand>();
                while (true)
                {
                    SkipWsAndComments(d, ref p);
                    if (p >= d.Length) return Err("unterminated array");
                    if (d[p] == ']') { p++; break; }
                    var t = ReadToken(d, ref p, depth + 1);
                    if (t.Error is not null) return t;
                    if (t.Operand is null) return Err("operator inside array");
                    items.Add(t.Operand);
                }
                return Tok(new Operand { Kind = OperandKind.Array, Items = items });
            }
            case (byte)']': case (byte)'>': case (byte)')': case (byte)'{': case (byte)'}':
                return Err("stray delimiter");
        }
        int s = p;
        while (p < d.Length && IsRegular(d[p])) p++;
        var span = new ReadOnlySpan<byte>(d, s, p - s);
        if (TryNumber(span, out double v)) return Tok(new Operand { Kind = OperandKind.Number, Number = v });
        string word = Encoding.Latin1.GetString(span);
        return word switch
        {
            "true" => Tok(new Operand { Kind = OperandKind.Bool, Bool = true }),
            "false" => Tok(new Operand { Kind = OperandKind.Bool, Bool = false }),
            "null" => Tok(new Operand { Kind = OperandKind.Null }),
            _ => new Token(null, word, null),
        };
    }

    private static Token ReadDict(byte[] d, ref int p, int depth)
    {
        p += 2;
        var items = new List<Operand>();
        while (true)
        {
            SkipWsAndComments(d, ref p);
            if (p >= d.Length) return Err("unterminated dictionary");
            if (d[p] == '>') { if (p + 1 < d.Length && d[p + 1] == '>') { p += 2; break; } return Err("stray >"); }
            var t = ReadToken(d, ref p, depth + 1);
            if (t.Error is not null) return t;
            if (t.Operand is null) return Err("operator inside dictionary");
            items.Add(t.Operand);
        }
        return Tok(new Operand { Kind = OperandKind.Dict, Items = items });
    }

    private static Token ReadLiteralString(byte[] d, ref int p)
    {
        p++;
        var buf = new List<byte>();
        int nest = 1;
        while (p < d.Length)
        {
            byte b = d[p++];
            if (b == '\\')
            {
                if (p >= d.Length) break;
                byte e = d[p++];
                switch (e)
                {
                    case (byte)'n': buf.Add(10); break;
                    case (byte)'r': buf.Add(13); break;
                    case (byte)'t': buf.Add(9); break;
                    case (byte)'b': buf.Add(8); break;
                    case (byte)'f': buf.Add(12); break;
                    case 13: if (p < d.Length && d[p] == 10) p++; break;
                    case 10: break;
                    case >= (byte)'0' and <= (byte)'7':
                    {
                        int v = e - '0'; int n = 1;
                        while (n < 3 && p < d.Length && d[p] >= '0' && d[p] <= '7') { v = v * 8 + (d[p++] - '0'); n++; }
                        buf.Add((byte)v);
                        break;
                    }
                    default: buf.Add(e); break;
                }
            }
            else if (b == '(') { nest++; buf.Add(b); }
            else if (b == ')') { if (--nest == 0) return Tok(new Operand { Kind = OperandKind.String, Bytes = buf.ToArray() }); buf.Add(b); }
            else buf.Add(b);
        }
        return Err("unterminated string");
    }

    private static Token ReadHexString(byte[] d, ref int p)
    {
        p++;
        var buf = new List<byte>();
        int hi = -1;
        while (p < d.Length)
        {
            byte b = d[p++];
            if (b == '>')
            {
                if (hi >= 0) buf.Add((byte)(hi * 16));
                return Tok(new Operand { Kind = OperandKind.String, Bytes = buf.ToArray() });
            }
            if (IsWs(b)) continue;
            int h = Hex(b);
            if (h < 0) return Err("bad hex string");
            if (hi < 0) hi = h; else { buf.Add((byte)(hi * 16 + h)); hi = -1; }
        }
        return Err("unterminated hex string");
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    private static bool TryNumber(ReadOnlySpan<byte> s, out double v)
    {
        v = 0;
        int i = 0; bool neg = false;
        if (s.Length == 0) return false;
        if (s[0] == '+' || s[0] == '-') { neg = s[0] == '-'; i = 1; }
        if (i >= s.Length) return false;
        double ip = 0, frac = 0, scale = 1; bool digits = false, dot = false;
        for (; i < s.Length; i++)
        {
            byte b = s[i];
            if (b >= '0' && b <= '9')
            {
                digits = true;
                if (!dot) ip = ip * 10 + (b - '0');
                else { scale /= 10; frac += (b - '0') * scale; }
            }
            else if (b == '.' && !dot) dot = true;
            else return false;
        }
        if (!digits) return false;
        v = (ip + frac) * (neg ? -1 : 1);
        return double.IsFinite(v);
    }

    /// <summary>`BI` 다음부터: 키/값 … `ID` 한 칸 데이터 … `EI`.</summary>
    private static (ContentOp?, string?) ReadInlineImage(byte[] d, ref int p, int start, int opStart)
    {
        var dict = new List<Operand>();
        while (true)
        {
            SkipWsAndComments(d, ref p);
            if (p >= d.Length) return (null, "inline image without ID");
            int ts = p;
            var t = ReadToken(d, ref p, 1);
            if (t.Error is not null) return (null, "inline image dict: " + t.Error);
            if (t.Operand is not null) { t.Operand.Start = ts; t.Operand.End = p; dict.Add(t.Operand); continue; }
            if (t.Keyword == "ID") break;
            return (null, "unexpected keyword in inline image dict");
        }
        // ID 뒤 공백 한 칸(CRLF는 한 칸으로)
        if (p < d.Length)
        {
            if (d[p] == 13 && p + 1 < d.Length && d[p + 1] == 10) p += 2;
            else if (IsWs(d[p])) p++;
        }
        int dataStart = p;
        for (int i = dataStart; i + 1 < d.Length; i++)
        {
            if (d[i] != 'E' || d[i + 1] != 'I') continue;
            if (i > dataStart && !IsWs(d[i - 1])) continue;
            if (i + 2 < d.Length && !(IsWs(d[i + 2]) || IsDelim(d[i + 2]))) continue;
            // EI 뒤 몇 바이트가 평문(연산자·숫자)이어야 진짜 EI
            bool plain = true;
            for (int k = i + 2; k < d.Length && k < i + 2 + 12; k++)
                if (d[k] > 126 || (d[k] < 32 && !IsWs(d[k]))) { plain = false; break; }
            if (!plain) continue;
            p = i + 2;
            return (new ContentOp { Name = "BI", Start = start, OpStart = opStart, End = p, Operands = dict.ToArray(), DataStart = dataStart, DataEnd = i }, null);
        }
        return (null, "inline image without EI");
    }
}
