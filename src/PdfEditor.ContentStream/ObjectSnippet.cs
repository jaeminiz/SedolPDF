using System.Text;

namespace PdfEditor.ContentStream;

/// <summary>조각이 쓰는 쪽 자원 하나. Category는 "Font" | "XObject" | "ExtGState" | "ColorSpace" | "Pattern" | "Shading" | "Properties".</summary>
public sealed record ResourceUse(string Category, string Name);

/// <summary>객체 하나를 혼자 서는 조각으로 꺼낸 결과. Failure가 null이면 성공.</summary>
public sealed record SnippetResult(string? Content, IReadOnlyList<ResourceUse> Uses, string? Failure)
{
    public bool Ok => Failure is null;
}

/// <summary>
/// 개체 복사(0.9.0)의 저장 재료: 쪽 객체 하나를 `q &lt;then&gt; cm &lt;객체 시점 CTM&gt; cm &lt;그리기 상태&gt; &lt;객체 연산자&gt; Q`로 꺼낸다.
/// 점 p는 p·CTM·then에 그려지므로 원본 객체를 then만큼 옮긴 모양이 된다(then은 쪽 공간 변환). 원본 연산자를 그대로 쓰므로 충실도가 100 %다.
/// </summary>
public static class ObjectSnippet
{
    private static readonly HashSet<string> StandardColorSpaces = ["DeviceGray", "DeviceRGB", "DeviceCMYK", "Pattern", "G", "RGB", "CMYK", "I", "Indexed"];

    public static SnippetResult Extract(ParsedContent pc, Segmentation seg, int objectIndex, Mat then, (double R, double G, double B)? textColor = null)
    {
        if (!pc.Ok) return Fail("content not parsed: " + pc.Failure);
        if (!seg.Ok) return Fail("segmentation failed: " + seg.Failure);
        if (objectIndex < 0 || objectIndex >= seg.Objects.Count) return Fail($"object index {objectIndex} out of range");
        if (!then.IsFinite) return Fail("non-finite transform");
        var obj = seg.Objects[objectIndex];
        if (!obj.Ctm.IsFinite) return Fail($"object {objectIndex}: non-finite CTM");
        var data = pc.Data; var ops = pc.Ops;
        var main = ops[obj.OpIndex];
        var uses = new List<ResourceUse>();
        string open = $"q {then.ToOperands()} cm {obj.Ctm.ToOperands()} cm ";

        string content;
        switch (obj.Kind)
        {
            case ObjectKind.Text:
            {
                var s = obj.Text!;
                if (!s.Movable || s.EtOp < 0 || s.FontOp < 0) return Fail($"object {objectIndex}: text not copyable (position unknown or state changed inside BT)");
                if (s.Tr >= 4) return Fail($"object {objectIndex}: text render mode {s.Tr} clips");
                string? color = null;
                if (textColor is { } c)
                {
                    string rgb = $"{Mat.Fmt(c.R)} {Mat.Fmt(c.G)} {Mat.Fmt(c.B)}";
                    color = s.Tr is 1 or 2 or 5 or 6 ? $"{rgb} rg {rgb} RG" : $"{rgb} rg";
                }
                CollectUses(ops, s.ReplayOps, uses);
                CollectUses(ops, [s.FontOp], uses);
                content = ContentRewriter.TextCopy(data, ops, main, s, open, color);
                break;
            }
            case ObjectKind.Path:
            {
                if (obj.HasOwnClip) return Fail($"object {objectIndex}: path also sets a clip");
                for (int k = obj.FirstOpIndex; k < obj.OpIndex; k++)
                    if (ops[k].Name is not ("m" or "l" or "c" or "v" or "y" or "re" or "h"))
                        return Fail($"object {objectIndex}: operator {ops[k].Name} inside path");
                var sb = new StringBuilder(open);
                sb.Append('\n');
                AppendOps(sb, data, ops, obj.ReplayOps);
                for (int k = obj.FirstOpIndex; k <= obj.OpIndex; k++) sb.Append(Slice(data, ops[k])).Append(' ');
                sb.Append("\nQ\n");
                CollectUses(ops, obj.ReplayOps, uses);
                content = sb.ToString();
                break;
            }
            case ObjectKind.Image or ObjectKind.Form or ObjectKind.Shading:
            {
                var sb = new StringBuilder(open);
                sb.Append('\n');
                AppendOps(sb, data, ops, obj.ReplayOps);
                sb.Append(Slice(data, main)).Append("\nQ\n");
                CollectUses(ops, obj.ReplayOps, uses);
                CollectUses(ops, [obj.OpIndex], uses);
                content = sb.ToString();
                break;
            }
            default:
                return Fail($"object {objectIndex}: unsupported kind {obj.Kind}");
        }
        return new SnippetResult(content, uses.Distinct().ToList(), null);
    }

    private static void AppendOps(StringBuilder sb, byte[] data, List<ContentOp> ops, int[] indices)
    {
        foreach (int k in indices) sb.Append(Slice(data, ops[k])).Append(' ');
        if (indices.Length > 0) sb.Append('\n');
    }

    private static void CollectUses(List<ContentOp> ops, IEnumerable<int> indices, List<ResourceUse> uses)
    {
        foreach (int k in indices)
        {
            var op = ops[k];
            switch (op.Name)
            {
                case "Tf": if (FirstName(op) is { } f) uses.Add(new("Font", f)); break;
                case "Do": if (LastName(op) is { } x) uses.Add(new("XObject", x)); break;
                case "gs": if (LastName(op) is { } g) uses.Add(new("ExtGState", g)); break;
                case "sh": if (LastName(op) is { } s) uses.Add(new("Shading", s)); break;
                case "cs" or "CS": if (LastName(op) is { } c && !StandardColorSpaces.Contains(c)) uses.Add(new("ColorSpace", c)); break;
                case "scn" or "SCN":
                    if (op.Operands.Count > 0 && op.Operands[^1].Kind == OperandKind.Name) uses.Add(new("Pattern", op.Operands[^1].Text));
                    break;
                case "BI":
                    for (int i = 0; i + 1 < op.Operands.Count; i += 2)
                        if (op.Operands[i].Text is "CS" or "ColorSpace" && op.Operands[i + 1] is { Kind: OperandKind.Name } v && !StandardColorSpaces.Contains(v.Text))
                            uses.Add(new("ColorSpace", v.Text));
                    break;
            }
        }
    }

    private static string? FirstName(ContentOp op) => op.Operands.Count > 0 && op.Operands[0].Kind == OperandKind.Name ? op.Operands[0].Text : null;
    private static string? LastName(ContentOp op) => op.Operands.Count > 0 && op.Operands[^1].Kind == OperandKind.Name ? op.Operands[^1].Text : null;

    /// <summary>
    /// 조각 안의 자원 이름 피연산자를 바꾼다(다른 쪽에 붙일 때 이름이 겹치는 경우). 연산자 단위로 읽어 이름 피연산자만 바꾸므로
    /// 문자열 안의 우연한 일치는 바뀌지 않는다. newNames에 없는 이름은 그대로 둔다.
    /// </summary>
    public static string Rename(string content, IReadOnlyDictionary<ResourceUse, string> newNames)
    {
        if (newNames.Count == 0) return content;
        var bytes = Encoding.Latin1.GetBytes(content);
        var pc = ContentLexer.Parse(bytes);
        var edits = new List<(int Start, int End, string Text)>();
        void Swap(Operand o, string category)
        {
            if (o.Kind == OperandKind.Name && newNames.TryGetValue(new ResourceUse(category, o.Text), out var nn)) edits.Add((o.Start, o.End, "/" + nn));
        }
        foreach (var op in pc.Ops)
        {
            if (op.Operands.Count == 0) continue;
            switch (op.Name)
            {
                case "Tf": Swap(op.Operands[0], "Font"); break;
                case "Do": Swap(op.Operands[^1], "XObject"); break;
                case "gs": Swap(op.Operands[^1], "ExtGState"); break;
                case "sh": Swap(op.Operands[^1], "Shading"); break;
                case "cs" or "CS": Swap(op.Operands[^1], "ColorSpace"); break;
                case "scn" or "SCN": Swap(op.Operands[^1], "Pattern"); break;
                case "BI":
                    for (int i = 0; i + 1 < op.Operands.Count; i += 2)
                        if (op.Operands[i].Text is "CS" or "ColorSpace") Swap(op.Operands[i + 1], "ColorSpace");
                    break;
            }
        }
        if (edits.Count == 0) return content;
        var sb = new StringBuilder();
        int pos = 0;
        foreach (var (s, e, t) in edits.OrderBy(x => x.Start))
        {
            sb.Append(Encoding.Latin1.GetString(bytes, pos, s - pos)).Append(t);
            pos = e;
        }
        sb.Append(Encoding.Latin1.GetString(bytes, pos, bytes.Length - pos));
        return sb.ToString();
    }

    private static string Slice(byte[] d, ContentOp op) => Encoding.Latin1.GetString(d, op.Start, op.End - op.Start);
    private static SnippetResult Fail(string why) => new(null, [], why);
}
