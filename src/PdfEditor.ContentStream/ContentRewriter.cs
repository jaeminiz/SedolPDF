using System.Text;

namespace PdfEditor.ContentStream;

/// <summary>객체에 하는 편집. 번호는 <see cref="Segmentation.Objects"/>의 번호(= 검증된 PDFium 객체 번호).</summary>
public abstract record ObjectEdit(int ObjectIndex);

/// <summary>
/// 객체를 안 보이게 한다(경로: 칠하기/선 → `n`, 글: `3 Tr`로 감싸 글자 폭은 유지, 그림·셰이딩: 삭제).
/// <paramref name="RemoveText"/>(M5 C12)이면 글은 가능할 때 보이기 연산자째 지워 문자열이 파일에 남지 않게 한다 — 뒤의 글 위치가 이 글의 이동량에 기대면 숨김으로 내려간다.
/// </summary>
public sealed record DisableObject(int ObjectIndex, bool RemoveText = false) : ObjectEdit(ObjectIndex);

/// <summary>객체를 쪽 공간 변환 <see cref="Then"/>(원래 모양에 이어서 적용)만큼 옮기거나 키운다.</summary>
public sealed record TransformObject(int ObjectIndex, Mat Then) : ObjectEdit(ObjectIndex);

/// <summary>글 보이기 연산자 안의 연속한 글 코드 범위(Str번째 문자열의 FromCode번째부터 Count개). AdvanceText = 그 글자들이 차지하던 글 공간 이동량(자간·낱말 간격 포함, Tz 적용 전).</summary>
public sealed record TextCut(int Str, int FromCode, int Count, double AdvanceText);

/// <summary>빼낸 글 범위 하나와 그 자리에 쓸 새 글. NewText가 null이면 새 글은 다른 경로(앱 글꼴 줄)가 쓴다. Tm = 새 글을 놓을 글 행렬(글 공간, CTM 제외).</summary>
public sealed record TextSpanReplace(IReadOnlyList<TextCut> Cuts, byte[]? NewText, Mat Tm);

/// <summary>
/// 글 객체 하나에서 찾은 글 범위를 제자리에서 바꾼다(0.4.0 찾아 바꾸기). 글 보이기 연산자를 `[… ] TJ`로 다시 써서 범위의 글 코드를 빼고(그 폭만큼 TJ 간격으로 메워 뒤 글의 위치는 그대로),
/// 새 글은 원래 글 객체의 `ET` 바로 뒤에 같은 글 상태로 덧붙인다(겹침 순서·클립 유지). 글이 비게 돼도 연산자는 남아(간격만) 객체 수는 그대로다.
/// Unit = 한 글 코드의 바이트 수(1 또는 2). `'`·`"` 연산자는 지원하지 않는다.
/// </summary>
public sealed record ReplaceTextSpans(int ObjectIndex, int Unit, IReadOnlyList<TextSpanReplace> Spans) : ObjectEdit(ObjectIndex);

public sealed class RewriteResult
{
    public bool Ok => Failure is null;
    public string? Failure { get; init; }
    public byte[]? Data { get; init; }
    /// <summary>연산자째 지운 글 객체 수(M5 C12).</summary>
    public int DeletedTextCount { get; init; }
    /// <summary>지우려 했지만 뒤 글이 이동량에 기대어 `3 Tr` 숨김으로 내려간 글 객체 수 — 0보다 크면 옛 글이 파일에 보이지 않는 글자로 남는다.</summary>
    public int HiddenTextCount { get; init; }
}

/// <summary>
/// 편집한 객체의 바이트만 고쳐 쓰고 나머지는 원본 바이트 그대로 둔다(충실도 100 %).
/// 옮기기: `q X cm … Q`, X = C·Then·C⁻¹ (C = 객체 시점 CTM) — 글은 BT 안에서 q/cm을 쓸 수 없으므로 원본을 보이지 않게 하고 ET 뒤에 같은 글을 변환해 다시 그린다.
/// 결과는 다시 해석해 q/Q·BT/ET 균형과 객체 구성을 확인하고, 하나라도 어긋나면 실패를 돌려준다(호출자는 원본을 그대로 둔다).
/// </summary>
public static class ContentRewriter
{
    private sealed record Piece(int Start, int End, byte[] Text, int Order, int Seq);

    private const int OrderClose = 0, OrderOpen = 1;

    public static RewriteResult Rewrite(ParsedContent pc, Segmentation seg, IReadOnlyList<ObjectEdit> edits,
        IReadOnlyList<byte[]>? appends = null, Func<string, XObjectInfo?>? xobjects = null)
    {
        if (!pc.Ok) return Fail("content not parsed: " + pc.Failure);
        if (!seg.Ok) return Fail("segmentation failed: " + seg.Failure);
        var data = pc.Data; var ops = pc.Ops;
        var pieces = new List<Piece>();
        int seq = 0;
        void Insert(int pos, string s, int order) => pieces.Add(new Piece(pos, pos, Latin1(s), order, seq++));
        void Replace(int start, int end, string s) => pieces.Add(new Piece(start, end, Latin1(s), OrderOpen, seq++));

        var seen = new HashSet<int>();
        int textCopies = 0, deletedText = 0, hiddenText = 0;
        var deletable = DeletableTexts(pc, seg, edits);
        var removedKinds = new Dictionary<ObjectKind, int>();
        foreach (var edit in edits)
        {
            if (edit.ObjectIndex < 0 || edit.ObjectIndex >= seg.Objects.Count) return Fail($"object index {edit.ObjectIndex} out of range");
            if (!seen.Add(edit.ObjectIndex)) return Fail($"object {edit.ObjectIndex} edited twice");
            var obj = seg.Objects[edit.ObjectIndex];
            var main = ops[obj.OpIndex];
            switch (edit)
            {
                case DisableObject:
                    switch (obj.Kind)
                    {
                        case ObjectKind.Path:
                            Replace(main.OpStart, main.End, "n");
                            Bump(removedKinds, ObjectKind.Path);
                            break;
                        case ObjectKind.Text:
                            if (obj.Text!.Tr >= 4) return Fail($"object {edit.ObjectIndex}: text render mode {obj.Text.Tr} clips; cannot hide");
                            if (edit is DisableObject { RemoveText: true })
                            {
                                if (deletable.Contains(obj.OpIndex))
                                {
                                    Replace(main.Start, main.End, ShowReplacement(data, main));
                                    Bump(removedKinds, ObjectKind.Text);
                                    deletedText++;
                                    break;
                                }
                                if (obj.Text.Tr != 3) hiddenText++;
                            }
                            if (obj.Text.Tr != 3) HideText(obj, main, Insert);
                            break;
                        default:
                            Replace(main.Start, main.End, " ");
                            Bump(removedKinds, obj.Kind);
                            break;
                    }
                    break;

                case TransformObject t:
                {
                    var c = obj.Ctm;
                    var inv = c.Invert();
                    if (inv is null) return Fail($"object {edit.ObjectIndex}: singular CTM");
                    if (!t.Then.IsFinite) return Fail("non-finite transform");
                    var x = c.Then(t.Then).Then(inv.Value);
                    if (!x.IsFinite) return Fail("non-finite matrix");
                    string open = $"q {x.ToOperands()} cm ";
                    switch (obj.Kind)
                    {
                        case ObjectKind.Path:
                            if (obj.HasOwnClip) return Fail($"object {edit.ObjectIndex}: path also sets a clip");
                            for (int k = obj.FirstOpIndex; k < obj.OpIndex; k++)
                                if (ops[k].Name is not ("m" or "l" or "c" or "v" or "y" or "re" or "h"))
                                    return Fail($"object {edit.ObjectIndex}: operator {ops[k].Name} inside path");
                            Insert(ops[obj.FirstOpIndex].Start, open, OrderOpen);
                            Insert(main.End, " Q", OrderClose);
                            break;
                        case ObjectKind.Text:
                        {
                            var s = obj.Text!;
                            if (!s.Movable || s.EtOp < 0) return Fail($"object {edit.ObjectIndex}: text not movable (position unknown or state changed inside BT)");
                            if (s.Tr >= 4) return Fail($"object {edit.ObjectIndex}: text render mode {s.Tr} clips");
                            if (s.Tr != 3) HideText(obj, main, Insert);
                            Insert(ops[s.EtOp].End, TextCopy(data, ops, main, s, open), OrderOpen);
                            textCopies++;
                            break;
                        }
                        default:
                            Insert(main.Start, open, OrderOpen);
                            Insert(main.End, " Q", OrderClose);
                            break;
                    }
                    break;
                }
                case ReplaceTextSpans rs:
                {
                    if (obj.Kind != ObjectKind.Text || obj.Text is not { } snap) return Fail($"object {edit.ObjectIndex}: not a text object");
                    if (main.Name is not ("Tj" or "TJ")) return Fail($"object {edit.ObjectIndex}: text operator {main.Name} is not supported for span replace");
                    if (snap.Tr >= 4) return Fail($"object {edit.ObjectIndex}: text render mode {snap.Tr} clips");
                    if (rs.Unit is not (1 or 2)) return Fail("bad code unit");
                    double fs = snap.FontOp >= 0 ? ops[snap.FontOp].Operands.LastOrDefault(o => o.Kind == OperandKind.Number)?.Number ?? 0 : 0;
                    if (fs <= 0) return Fail($"object {edit.ObjectIndex}: font size unknown");
                    string? spliced = SpliceTextOp(main, rs, fs, out bool glyphsLeft);
                    if (spliced is null) return Fail($"object {edit.ObjectIndex}: text cut out of range");
                    Replace(main.Start, main.End, spliced);
                    if (!glyphsLeft) Bump(removedKinds, ObjectKind.Text);   // 글자가 하나도 안 남은 글 보이기는 객체가 아니다(V16 규칙)
                    if (rs.Spans.Any(sp => sp.NewText is not null))
                    {
                        if (snap.EtOp < 0 || snap.FontOp < 0) return Fail($"object {edit.ObjectIndex}: no ET or font for the new text");
                        for (int k = snap.BtOp + 1; k < snap.EtOp; k++)
                            if (ops[k].Name is "q" or "Q" or "cm") return Fail($"object {edit.ObjectIndex}: q/Q/cm inside the text object");
                        foreach (var sp in rs.Spans.Where(x => x.NewText is not null))
                        {
                            Insert(ops[snap.EtOp].End, NewTextBlock(data, ops, snap, sp), OrderOpen);
                            textCopies++;
                        }
                    }
                    break;
                }
                default: return Fail("unknown edit");
            }
        }

        var composed = Compose(data, pieces);
        if (composed is null) return Fail("overlapping edits");

        var origProfile = Profile(ops);
        var reparsed = ContentLexer.Parse(composed);
        if (!reparsed.Ok) return Fail("result does not parse: " + reparsed.Failure);
        if (Profile(reparsed.Ops) != origProfile) return Fail("q/Q or BT/ET balance changed");

        if (appends is null or { Count: 0 })
        {
            var re = ContentSegmenter.Segment(reparsed, xobjects);
            if (!re.Ok) return Fail("result does not segment: " + re.Failure);
            var expected = Counts(seg.Objects.Select(o => o.Kind));
            foreach (var (k, n) in removedKinds) expected[k] = expected.GetValueOrDefault(k) - n;
            // 글 옮기기: 원본은 남고(보이지 않게) 변환한 사본이 하나 늘어난다.
            expected[ObjectKind.Text] = expected.GetValueOrDefault(ObjectKind.Text) + textCopies;
            var actual = Counts(re.Objects.Select(o => o.Kind));
            foreach (var k in Enum.GetValues<ObjectKind>())
                if (expected.GetValueOrDefault(k) != actual.GetValueOrDefault(k)) return Fail($"object count of {k} is {actual.GetValueOrDefault(k)}, expected {expected.GetValueOrDefault(k)}");
            return new RewriteResult { Data = composed, DeletedTextCount = deletedText, HiddenTextCount = hiddenText };
        }

        var final = Append(composed, origProfile, appends);
        if (final is null) return Fail("cannot append: unbalanced content");
        var check = ContentLexer.Parse(final);
        if (!check.Ok) return Fail("appended result does not parse: " + check.Failure);
        return new RewriteResult { Data = final, DeletedTextCount = deletedText, HiddenTextCount = hiddenText };
    }

    /// <summary>쪽 맨 끝에 `q … Q`로 감싼 조각을 덧붙인다(앞 상태를 이어받지 않도록 필요하면 열려 있는 q/BT를 먼저 닫는다).</summary>
    public static RewriteResult AppendOnly(ParsedContent pc, IReadOnlyList<byte[]> snippets)
    {
        if (!pc.Ok) return Fail("content not parsed: " + pc.Failure);
        var p = Profile(pc.Ops);
        var final = Append(pc.Data, p, snippets);
        if (final is null) return Fail("cannot append: unbalanced content");
        var check = ContentLexer.Parse(final);
        return check.Ok ? new RewriteResult { Data = final } : Fail("appended result does not parse: " + check.Failure);
    }

    // ---- 내부 ----

    /// <summary>
    /// 연산자째 지워도 뒤 글 위치가 변하지 않는 글 보이기 연산자 번호들(C12). 지울 글 바로 뒤의 글 보이기가 Tm/Td/TD/T*/'/"로 자리를 새로 잡거나 ET/BT(또는 끝)가 먼저 오면 안전하고,
    /// Tj/TJ가 그대로 이어 쓰면 그 글이 이 글의 이동량에 기댄다 — 이어 쓰는 글도 함께 지워지고 그 글도 안전할 때만 안전하다.
    /// </summary>
    private static HashSet<int> DeletableTexts(ParsedContent pc, Segmentation seg, IReadOnlyList<ObjectEdit> edits)
    {
        var remove = new SortedSet<int>();
        foreach (var e in edits)
            if (e is DisableObject { RemoveText: true } && e.ObjectIndex >= 0 && e.ObjectIndex < seg.Objects.Count
                && seg.Objects[e.ObjectIndex] is { Kind: ObjectKind.Text, Text: { Tr: < 4 } } o)
                remove.Add(o.OpIndex);
        var safe = new HashSet<int>();
        var ops = pc.Ops;
        foreach (int i in remove.Reverse())
        {
            bool ok = true;
            for (int j = i + 1; j < ops.Count; j++)
            {
                string n = ops[j].Name;
                if (n is "Tm" or "Td" or "TD" or "T*" or "'" or "\"" or "ET" or "BT") break;
                if (n is "Tj" or "TJ") { ok = remove.Contains(j) && safe.Contains(j); break; }
            }
            if (ok) safe.Add(i);
        }
        return safe;
    }

    /// <summary>글 보이기 연산자를 지운 자리에 남길 것: `'`는 줄 이동만(`T*`), `"`는 낱말·글자 간격 지정과 줄 이동, 나머지는 빈칸.</summary>
    private static string ShowReplacement(byte[] data, ContentOp show)
    {
        if (show.Name == "'") return "T*";
        if (show.Name == "\"" && show.Operands.Count >= 3)
            return $"{Latin1S(data, show.Operands[0].Start, show.Operands[0].End)} Tw {Latin1S(data, show.Operands[1].Start, show.Operands[1].End)} Tc T*";
        return " ";
    }

    private static void HideText(PageObject obj, ContentOp show, Action<int, string, int> insert)
    {
        insert(show.Start, "3 Tr ", OrderOpen);
        insert(show.End, $" {obj.Text!.Tr} Tr", OrderClose);
    }

    private static string TextCopy(byte[] data, List<ContentOp> ops, ContentOp show, TextSnapshot s, string open)
    {
        var sb = new StringBuilder();
        sb.Append('\n').Append(open);
        foreach (int k in s.ReplayOps) sb.Append(Slice(data, ops[k])).Append(' ');
        sb.Append(Slice(data, ops[s.FontOp])).Append(' ');
        sb.Append($"{Mat.Fmt(s.Tc)} Tc {Mat.Fmt(s.Tw)} Tw {Mat.Fmt(s.Tz)} Tz {Mat.Fmt(s.TL)} TL {Mat.Fmt(s.Ts)} Ts {s.Tr} Tr ");
        sb.Append($"BT {s.Tm.ToOperands()} Tm ");
        if (show.Name is "'" or "\"")
        {
            var str = show.Operands[^1];
            sb.Append(Latin1S(data, str.Start, str.End)).Append(" Tj");
        }
        else sb.Append(Slice(data, show));
        sb.Append(" ET Q\n");
        return sb.ToString();
    }

    /// <summary>글 보이기 연산자를 잘라낼 범위를 뺀 `[… ] TJ`로 다시 쓴다. 범위가 문자열 밖이면 null.</summary>
    private static string? SpliceTextOp(ContentOp show, ReplaceTextSpans rs, double fontSize, out bool glyphsLeft)
    {
        glyphsLeft = false;
        var items = new List<Operand>();
        if (show.Name == "TJ") { foreach (var o in show.Operands) if (o.Kind == OperandKind.Array) items.AddRange(o.Items); }
        else if (show.Operands.Count > 0) items.Add(show.Operands[^1]);
        var cuts = rs.Spans.SelectMany(s => s.Cuts).GroupBy(c => c.Str).ToDictionary(g => g.Key, g => g.OrderBy(c => c.FromCode).ToList());
        int stringCount = items.Count(i => i.Kind == OperandKind.String);
        if (cuts.Keys.Any(k => k < 0 || k >= stringCount)) return null;
        var sb = new StringBuilder("["); int left = 0; glyphsLeft = false;
        int str = -1;
        foreach (var it in items)
        {
            if (it.Kind == OperandKind.Number) { sb.Append(Mat.Fmt(it.Number)).Append(' '); continue; }
            if (it.Kind != OperandKind.String) continue;
            str++;
            int n = it.Bytes.Length / rs.Unit;
            if (!cuts.TryGetValue(str, out var list)) { left += AppendHex(sb, it.Bytes, 0, it.Bytes.Length); continue; }
            int pos = 0;
            foreach (var c in list)
            {
                if (c.FromCode < pos || c.Count <= 0 || c.FromCode + c.Count > n) return null;
                left += AppendHex(sb, it.Bytes, pos * rs.Unit, (c.FromCode - pos) * rs.Unit);
                sb.Append(Mat.Fmt(-c.AdvanceText * 1000.0 / fontSize)).Append(' ');
                pos = c.FromCode + c.Count;
            }
            left += AppendHex(sb, it.Bytes, pos * rs.Unit, (n - pos) * rs.Unit + (it.Bytes.Length - n * rs.Unit));
        }
        sb.Append("] TJ"); glyphsLeft = left > 0;
        return sb.ToString();
    }

    private static int AppendHex(StringBuilder sb, byte[] bytes, int offset, int count)
    {
        if (count <= 0) return 0;
        sb.Append('<').Append(Convert.ToHexString(bytes, offset, count)).Append("> ");
        return count;
    }

    /// <summary>바꾼 새 글: 원래 글 객체와 같은 글 상태(색·gs·글꼴·자간·가로 비율·렌더 모드)로 `q … Q` 안에 쓴다. ET 바로 뒤에 넣는다.</summary>
    private static string NewTextBlock(byte[] data, List<ContentOp> ops, TextSnapshot s, TextSpanReplace sp)
    {
        var sb = new StringBuilder();
        sb.Append("\nq ");
        foreach (int k in s.ReplayOps) sb.Append(Slice(data, ops[k])).Append(' ');
        sb.Append(Slice(data, ops[s.FontOp])).Append(' ');
        sb.Append($"{Mat.Fmt(s.Tc)} Tc {Mat.Fmt(s.Tw)} Tw {Mat.Fmt(s.Tz)} Tz {Mat.Fmt(s.TL)} TL {Mat.Fmt(s.Ts)} Ts {s.Tr} Tr ");
        sb.Append($"BT {sp.Tm.ToOperands()} Tm <{Convert.ToHexString(sp.NewText!)}> Tj ET Q\n");
        return sb.ToString();
    }

    private static string Slice(byte[] d, ContentOp op) => Latin1S(d, op.Start, op.End);
    private static string Latin1S(byte[] d, int s, int e) => Encoding.Latin1.GetString(d, s, e - s);
    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);
    private static RewriteResult Fail(string why) => new() { Failure = why };
    private static void Bump(Dictionary<ObjectKind, int> d, ObjectKind k) => d[k] = d.GetValueOrDefault(k) + 1;
    private static Dictionary<ObjectKind, int> Counts(IEnumerable<ObjectKind> kinds) =>
        kinds.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());

    private static byte[]? Compose(byte[] data, List<Piece> pieces)
    {
        var sorted = pieces.OrderBy(p => p.Start).ThenBy(p => p.Order).ThenBy(p => p.Seq).ToList();
        var ms = new MemoryStream(data.Length + 256);
        int pos = 0;
        foreach (var p in sorted)
        {
            if (p.Start < pos) return null;   // 바꾸는 구간이 겹침
            ms.Write(data, pos, p.Start - pos);
            ms.Write(p.Text);
            pos = p.End;
        }
        ms.Write(data, pos, data.Length - pos);
        return ms.ToArray();
    }

    /// <summary>(끝 q 깊이, 가장 낮았던 깊이, BT 끝 깊이, BT 가장 깊었던 값).</summary>
    private readonly record struct BalanceProfile(int FinalDepth, int MinDepth, int FinalBt, int MaxBt);

    private static BalanceProfile Profile(List<ContentOp> ops)
    {
        int d = 0, min = 0, bt = 0, maxBt = 0;
        foreach (var op in ops)
        {
            switch (op.Name)
            {
                case "q": d++; break;
                case "Q": d--; min = Math.Min(min, d); break;
                case "BT": bt++; maxBt = Math.Max(maxBt, bt); break;
                case "ET": bt = Math.Max(0, bt - 1); break;
            }
        }
        return new BalanceProfile(d, min, bt, maxBt);
    }

    private static byte[]? Append(byte[] data, BalanceProfile p, IReadOnlyList<byte[]> snippets)
    {
        // 덧붙인 조각은 쪽 첫머리와 같은 기본 상태에서 그려져야 한다(PDFium이 넣은 새 객체와 같게). 원본이 gs·색·cm을 q/Q 없이 남기고 끝나는 일이 흔하므로
        // (예: 투명도·혼합 모드를 쪽 전체에 건 PDF) 원본을 항상 q … Q로 감싼다. 원본이 q를 더 많이 닫는 파손이면 감쌀 수 없어 거절한다.
        if (p.MinDepth < 0) return null;
        var ms = new MemoryStream();
        ms.Write(Latin1("q\n"));
        ms.Write(data);
        ms.Write(Latin1("\n"));
        for (int i = 0; i < p.FinalBt; i++) ms.Write(Latin1("ET\n"));
        for (int i = 0; i < 1 + p.FinalDepth; i++) ms.Write(Latin1("Q\n"));
        foreach (var s in snippets)
        {
            ms.Write(Latin1("\nq\n"));
            ms.Write(s);
            ms.Write(Latin1("\nQ\n"));
        }
        return ms.ToArray();
    }
}
