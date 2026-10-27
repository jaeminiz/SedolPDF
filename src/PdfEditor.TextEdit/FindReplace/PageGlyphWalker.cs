using PdfEditor.ContentStream;
using PdfEditor.ContentStream.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using SharpPage = PdfSharp.Pdf.PdfPage;

namespace PdfEditor.TextEdit.FindReplace;

/// <summary>
/// 쪽 내용을 따라가며 얻은 글자 하나(0.4.0 찾기 엔진). 위치는 쪽 사용자 공간(회전 전)이다.
/// StreamId 0 = 쪽 내용 스트림, 1 이상 = 폼 XObject 안(찾을 수만 있고 바꿀 수 없다, V15).
/// </summary>
public sealed class GlyphInfo
{
    public string Text = "";
    public int StreamId, Op, Str, Code, RawCode;
    /// <summary>글 공간 이동량(글자 폭 + 자간 + 낱말 간격, Tz 적용 전).</summary>
    public double AdvText;
    /// <summary>기준선 시작·끝(쪽 공간, 폭만, 자간 제외).</summary>
    public double X0, Y0, X1, Y1;
    /// <summary>쪽 공간에서의 글자 크기(pt).</summary>
    public double Size;
    /// <summary>쪽 공간에서의 글 방향(도, 0~359).</summary>
    public int Angle;
    public bool Hidden, Virtual;
    /// <summary>2x2 = 글 행렬×CTM, 이동 = 기준선 시작점.</summary>
    public Mat Place;
    /// <summary>글자를 보인 시점의 글 행렬(글 공간, CTM·올림 제외)과 CTM.</summary>
    public Mat Tm, Ctm;
    public double FontSize, Tz = 100, Tc, Tw, Ts;
    /// <summary>글자 렌더 모드(Tr).</summary>
    public int Tr;
    public string FontRes = "";
    public FontInfo? Font;
    public bool InForm => StreamId != 0;
    /// <summary>`'`·`"` 연산자로 보인 글자(바꾸기 미지원).</summary>
    public bool QuoteOp;
}

/// <summary>쪽 안에서 쓰인 글꼴 하나(글꼴 사전 하나).</summary>
public sealed class FontInfo
{
    public string Res = "";
    public PdfDictionary Dict = null!;
    public bool Type0, Type3, Embedded;
    public FontCodebook? Book;
    public double[]? Widths;
    public int First;
    public double Missing = 500;
    /// <summary>글자 폭 표가 없어 기본 폭으로 어림한 단순 글꼴(위치가 어긋날 수 있다).</summary>
    public bool ApproxWidths => !Type0 && Widths is null;
    /// <summary>글 코드를 글자로 풀 수 있는가(없으면 찾을 수 없는 글꼴 — 집계만 한다).</summary>
    public bool Searchable => !Type3 && (Book is not null || !Type0);
    public int Unit => Type0 ? 2 : 1;

    public IEnumerable<(int Code, string Text, double W)> Glyphs(byte[] bytes)
    {
        if (Type0)
        {
            if (Book is null) yield break;
            foreach (int c in Book.Codes(bytes))
            {
                string t = Book.TextOfCode(c) ?? "�";
                double w = Book.WidthOfCode(c);
                yield return (c, t, w <= 0 ? 1000 : w);
            }
            yield break;
        }
        foreach (byte b in bytes)
        {
            string t = Book?.TextOfCode(b) ?? ((char)b).ToString();
            double w = Widths is not null && b - First >= 0 && b - First < Widths.Length ? Widths[b - First] : Missing;
            yield return (b, t, w);
        }
    }
}

/// <summary>쪽 내용을 걸어 본 결과.</summary>
public sealed class PageGlyphWalk
{
    public List<GlyphInfo> Glyphs = [];
    /// <summary>글 보이기 연산자 수 / 글꼴을 풀 수 없어 건너뛴 수(찾을 수 없음 집계).</summary>
    public int ShowOps, UnsearchableOps, QuoteOps;
    /// <summary>찾을 수 없는 글 연산 수(글꼴 자원 이름별).</summary>
    public Dictionary<string, int> UnsearchableByFont = [];
    /// <summary>스트림 번호 → 해석된 내용(0 = 쪽).</summary>
    public Dictionary<int, ParsedContent> Streams = [];
    public List<FontInfo> Fonts = [];
    public string? Failure;
    public bool Ok => Failure is null;
}

/// <summary>
/// 쪽 내용을 처음부터 따라가며(q/Q/cm, 글 상태, 글꼴 폭) 글자마다 쪽 공간 위치를 구한다(0.4.0 D9: <c>PageObject.Text</c>(TextSnapshot)는 Tj 사슬 뒤의 Tm을 몰라 쓸 수 없다).
/// PDFium을 부르지 않는다. 폼 XObject는 <c>Do</c>를 따라 들어간다(깊이 4).
/// </summary>
public static class PageGlyphWalker
{
    private const int MaxFormDepth = 4;

    private readonly record struct GState(Mat Ctm, double Tc, double Tw, double Tz, double Tl, double Ts, int Tr, FontInfo? Font, string FontRes, double Fs);

    private sealed class Scope(PdfDictionary? fonts, PdfDictionary? xobjects)
    {
        public PdfDictionary? Fonts = fonts;
        public PdfDictionary? XObjects = xobjects;
    }

    public static PageGlyphWalk Walk(SharpPage page)
    {
        var res = new PageGlyphWalk();
        var loaded = PageContent.Load(page);
        if (loaded.Data is null) { res.Failure = loaded.Failure ?? "contents"; return res; }
        var pc = ContentLexer.Parse(loaded.Data);
        if (!pc.Ok) { res.Failure = pc.Failure; return res; }
        res.Streams[0] = pc;
        var scope = ScopeOf(page.Resources);

        // 1차: 글꼴 사전마다 쪽(폼 포함)에서 보인 문자열을 모아 글꼴을 만든다(같은 사전은 하나).
        var shown = new Dictionary<PdfDictionary, List<ReadOnlyMemory<byte>>>(ReferenceEqualityComparer.Instance);
        CollectShown(pc, scope, shown, 0, []);
        var fonts = new Dictionary<PdfDictionary, FontInfo>(ReferenceEqualityComparer.Instance);
        foreach (var (dict, strs) in shown)
        {
            var f = new FontInfo { Dict = dict, Type0 = dict.Elements.GetName("/Subtype") == "/Type0", Type3 = dict.Elements.GetName("/Subtype") == "/Type3" };
            if (!f.Type3)
            {
                f.Book = FontCodebook.TryBuild(dict, strs);
                if (!f.Type0)
                {
                    var w = dict.Elements.GetArray("/Widths");
                    if (w is not null)
                    {
                        f.First = dict.Elements.ContainsKey("/FirstChar") ? dict.Elements.GetInteger("/FirstChar") : 0;
                        f.Widths = w.Elements.Select(e => (e is PdfReference r ? r.Value : e) switch { PdfInteger i => (double)i.Value, PdfReal d => d.Value, _ => 500 }).ToArray();
                    }
                    var fd = dict.Elements.GetDictionary("/FontDescriptor");
                    f.Embedded = fd is not null && (fd.Elements.ContainsKey("/FontFile") || fd.Elements.ContainsKey("/FontFile2") || fd.Elements.ContainsKey("/FontFile3"));
                    if (fd is not null && fd.Elements.ContainsKey("/MissingWidth")) f.Missing = fd.Elements.GetReal("/MissingWidth");
                }
            }
            fonts[dict] = f;
            res.Fonts.Add(f);
        }

        // 2차: 글 상태를 따라가며 글자 위치.
        int nextStream = 1;
        WalkStream(res, pc, scope, 0, ref nextStream, new GState(Mat.Identity, 0, 0, 100, 0, 0, 0, null, "", 0), fonts, 0, []);
        return res;
    }

    /// <summary>쪽(폼 포함)에서 글꼴 사전마다 보인 글 문자열을 모은다 — 문서 단위 코드북 재료.</summary>
    public static Dictionary<PdfDictionary, List<ReadOnlyMemory<byte>>> CollectShownStrings(SharpPage page)
    {
        var shown = new Dictionary<PdfDictionary, List<ReadOnlyMemory<byte>>>(ReferenceEqualityComparer.Instance);
        var loaded = PageContent.Load(page);
        if (loaded.Data is null) return shown;
        var pc = ContentLexer.Parse(loaded.Data);
        if (pc.Ok) CollectShown(pc, ScopeOf(page.Resources), shown, 0, []);
        return shown;
    }

    private static Scope ScopeOf(PdfDictionary? resources) =>
        new(resources?.Elements.GetDictionary("/Font"), resources?.Elements.GetDictionary("/XObject"));

    private static PdfDictionary? FontDict(Scope sc, string name)
    {
        try { return sc.Fonts?.Elements.GetDictionary("/" + name); } catch { return null; }
    }

    private static void CollectShown(ParsedContent pc, Scope sc, Dictionary<PdfDictionary, List<ReadOnlyMemory<byte>>> shown, int depth, HashSet<string> visiting)
    {
        string? cur = null;
        // 상태 스택: Tf는 q/Q로 저장·복원된다.
        var stack = new Stack<string?>();
        foreach (var op in pc.Ops)
        {
            switch (op.Name)
            {
                case "q": stack.Push(cur); break;
                case "Q": if (stack.Count > 0) cur = stack.Pop(); break;
                case "Tf": cur = op.Operands.FirstOrDefault(o => o.Kind == OperandKind.Name)?.Text; break;
                case "Tj": case "TJ": case "'": case "\"":
                {
                    if (cur is null) break;
                    var fd = FontDict(sc, cur);
                    if (fd is null) break;
                    if (!shown.TryGetValue(fd, out var l)) shown[fd] = l = [];
                    foreach (var o in op.Operands) CollectStrings(o, l);
                    break;
                }
                case "Do":
                {
                    if (depth >= MaxFormDepth || op.Operands.Count == 0 || op.Operands[^1].Kind != OperandKind.Name) break;
                    string name = op.Operands[^1].Text;
                    var form = LoadForm(sc, name, out var fscope, out _);
                    if (form is null || !visiting.Add(name)) break;
                    try { CollectShown(form, fscope ?? sc, shown, depth + 1, visiting); }
                    finally { visiting.Remove(name); }
                    break;
                }
            }
        }
    }

    private static ParsedContent? LoadForm(Scope sc, string name, out Scope? formScope, out Mat matrix)
    {
        formScope = null; matrix = Mat.Identity;
        try
        {
            var x = sc.XObjects?.Elements.GetDictionary("/" + name);
            if (x is null || x.Elements.GetName("/Subtype") != "/Form" || x.Stream is null) return null;
            var ma = x.Elements.GetArray("/Matrix");
            if (ma is { Elements.Count: 6 }) matrix = new Mat(ma.Elements.GetReal(0), ma.Elements.GetReal(1), ma.Elements.GetReal(2), ma.Elements.GetReal(3), ma.Elements.GetReal(4), ma.Elements.GetReal(5));
            var r = x.Elements.GetDictionary("/Resources");
            if (r is not null) formScope = ScopeOf(r);
            var pc = ContentLexer.Parse(x.Stream.UnfilteredValue);
            return pc.Ok ? pc : null;
        }
        catch { return null; }
    }

    private static void CollectStrings(Operand o, List<ReadOnlyMemory<byte>> into)
    {
        if (o.Kind == OperandKind.String) into.Add(o.Bytes);
        else if (o.Kind == OperandKind.Array) foreach (var x in o.Items) CollectStrings(x, into);
    }

    private static void WalkStream(PageGlyphWalk res, ParsedContent pc, Scope sc, int streamId, ref int nextStream, GState start,
        Dictionary<PdfDictionary, FontInfo> fonts, int depth, HashSet<string> visiting)
    {
        var stack = new Stack<GState>();
        var g = start;
        Mat tm = Mat.Identity, tlm = Mat.Identity;
        for (int i = 0; i < pc.Ops.Count; i++)
        {
            var op = pc.Ops[i];
            switch (op.Name)
            {
                case "q": stack.Push(g); break;
                case "Q": if (stack.Count > 0) g = stack.Pop(); break;
                case "cm": if (Nums(op, 6, out var m)) g = g with { Ctm = new Mat(m[0], m[1], m[2], m[3], m[4], m[5]).Then(g.Ctm) }; break;
                case "BT": tm = tlm = Mat.Identity; break;
                case "Tc": if (Nums(op, 1, out var a1)) g = g with { Tc = a1[0] }; break;
                case "Tw": if (Nums(op, 1, out var a2)) g = g with { Tw = a2[0] }; break;
                case "Tz": if (Nums(op, 1, out var a3)) g = g with { Tz = a3[0] }; break;
                case "TL": if (Nums(op, 1, out var a4)) g = g with { Tl = a4[0] }; break;
                case "Ts": if (Nums(op, 1, out var a5)) g = g with { Ts = a5[0] }; break;
                case "Tr": if (Nums(op, 1, out var a6)) g = g with { Tr = (int)a6[0] }; break;
                case "Tf":
                {
                    string? n = op.Operands.FirstOrDefault(o => o.Kind == OperandKind.Name)?.Text;
                    var fd = n is null ? null : FontDict(sc, n);
                    FontInfo? fi = null;
                    if (fd is not null) fonts.TryGetValue(fd, out fi);
                    if (fi is not null && fi.Res.Length == 0) fi.Res = n!;
                    g = g with { Font = fi, FontRes = n ?? "", Fs = op.Operands.LastOrDefault(o => o.Kind == OperandKind.Number)?.Number ?? 0 };
                    break;
                }
                case "Tm": if (Nums(op, 6, out var m6)) tm = tlm = new Mat(m6[0], m6[1], m6[2], m6[3], m6[4], m6[5]); break;
                case "Td": case "TD":
                    if (Nums(op, 2, out var t2)) { if (op.Name == "TD") g = g with { Tl = -t2[1] }; tlm = Mat.Translate(t2[0], t2[1]).Then(tlm); tm = tlm; }
                    break;
                case "T*": tlm = Mat.Translate(0, -g.Tl).Then(tlm); tm = tlm; break;
                case "Do":
                {
                    if (depth >= MaxFormDepth || op.Operands.Count == 0 || op.Operands[^1].Kind != OperandKind.Name) break;
                    string name = op.Operands[^1].Text;
                    var form = LoadForm(sc, name, out var fscope, out var fm);
                    if (form is null || !visiting.Add(name)) break;
                    try
                    {
                        int id = nextStream++;
                        res.Streams[id] = form;
                        WalkStream(res, form, fscope ?? sc, id, ref nextStream, g with { Ctm = fm.Then(g.Ctm) }, fonts, depth + 1, visiting);
                    }
                    finally { visiting.Remove(name); }
                    break;
                }
                case "Tj": case "TJ": case "'": case "\"":
                {
                    bool quote = op.Name is "'" or "\"";
                    if (quote)
                    {
                        res.QuoteOps++;
                        if (op.Name == "\"" && op.Operands.Count >= 3 && op.Operands[0].Kind == OperandKind.Number && op.Operands[1].Kind == OperandKind.Number)
                            g = g with { Tw = op.Operands[0].Number, Tc = op.Operands[1].Number };
                        tlm = Mat.Translate(0, -g.Tl).Then(tlm); tm = tlm;
                    }
                    var items = new List<Operand>();
                    if (op.Name == "TJ") { foreach (var o in op.Operands) if (o.Kind == OperandKind.Array) items.AddRange(o.Items); }
                    else if (op.Operands.Count > 0) items.Add(op.Operands[^1]);
                    if (!items.Any(it => it.Kind == OperandKind.String && it.Bytes.Length > 0)) break;   // 빈 글 보이기는 글 객체가 아니다
                    res.ShowOps++;
                    var font = g.Font;
                    if (font is null || !font.Searchable)
                    {
                        res.UnsearchableOps++;
                        string key = g.FontRes.Length == 0 ? "?" : g.FontRes;
                        res.UnsearchableByFont[key] = res.UnsearchableByFont.GetValueOrDefault(key) + 1;
                        break;   // 폭을 몰라 뒤 위치도 모른다 → 이 연산은 건너뜀
                    }
                    int strIdx = -1;
                    foreach (var it in items)
                    {
                        if (it.Kind == OperandKind.Number) { tm = Mat.Translate(-it.Number / 1000.0 * g.Fs * g.Tz / 100.0, 0).Then(tm); continue; }
                        if (it.Kind != OperandKind.String) continue;
                        strIdx++;
                        int codeIdx = 0;
                        foreach (var (code, text, w) in font.Glyphs(it.Bytes))
                        {
                            bool space = !font.Type0 && code == 32;
                            double adv = w / 1000.0 * g.Fs + g.Tc + (space ? g.Tw : 0);
                            var full = Mat.Translate(0, g.Ts).Then(tm).Then(g.Ctm);
                            var (sx, sy) = full.Apply(0, 0);
                            var (ex, ey) = full.Apply(w / 1000.0 * g.Fs * g.Tz / 100.0, 0);
                            double ang = Math.Atan2(full.B, full.A) * 180 / Math.PI;
                            res.Glyphs.Add(new GlyphInfo
                            {
                                Text = text, StreamId = streamId, Op = i, Str = strIdx, Code = codeIdx++, RawCode = code, AdvText = adv,
                                X0 = sx, Y0 = sy, X1 = ex, Y1 = ey,
                                Size = g.Fs * Math.Sqrt(full.C * full.C + full.D * full.D),
                                Angle = (int)Math.Round(((ang % 360) + 360) % 360) % 360,
                                Hidden = g.Tr is 3 or 7, Tr = g.Tr,
                                Place = new Mat(full.A, full.B, full.C, full.D, sx, sy),
                                Tm = tm, Ctm = g.Ctm,
                                FontSize = g.Fs, Tz = g.Tz, Tc = g.Tc, Tw = g.Tw, Ts = g.Ts, FontRes = g.FontRes, Font = font, QuoteOp = quote,
                            });
                            tm = Mat.Translate(adv * g.Tz / 100.0, 0).Then(tm);
                        }
                    }
                    break;
                }
            }
        }
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
}
