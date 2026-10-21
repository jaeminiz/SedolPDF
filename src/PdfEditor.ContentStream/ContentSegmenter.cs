namespace PdfEditor.ContentStream;

/// <summary>PDFium <c>PdfPageObjectType</c>과 같은 값(Text=1, Path=2, Image=3, Shading=4, Form=5).</summary>
public enum ObjectKind { Text = 1, Path = 2, Image = 3, Shading = 4, Form = 5 }

public readonly record struct Box(double L, double B, double R, double T)
{
    public double Width => R - L;
    public double Height => T - B;
}

/// <summary>텍스트 객체를 다른 자리에 다시 그리기 위해 보관하는 그 시점의 글 상태. 연산자는 원본 연산자 번호로 가리킨다.</summary>
public sealed class TextSnapshot
{
    public Mat Tm { get; init; }
    public double Tc { get; init; }
    public double Tw { get; init; }
    public double Tz { get; init; }
    public double TL { get; init; }
    public double Ts { get; init; }
    public int Tr { get; init; }
    /// <summary>글꼴을 정한 마지막 `Tf` 연산자 번호(-1이면 모름).</summary>
    public int FontOp { get; init; }
    /// <summary>같은 모양을 내려면 다시 보내야 하는 상태 연산자(선 굵기·gs·색 …)의 번호들.</summary>
    public int[] ReplayOps { get; init; } = [];
    public int BtOp { get; init; }
    /// <summary>짝이 되는 `ET`의 연산자 번호(-1이면 없음). 분할이 끝난 뒤 채운다.</summary>
    public int EtOp { get; internal set; } = -1;
    /// <summary>글 위치(Tm)를 알 수 있고 BT 안에서 cm/q/Q가 없었는가 — 거짓이면 옮기기 불가.</summary>
    public bool Movable { get; init; }
}

/// <summary>쪽 객체 하나(PDFium 객체 목록의 한 항목에 대응).</summary>
public sealed class PageObject
{
    public ObjectKind Kind { get; init; }
    /// <summary>그리는 연산자(경로: 칠하기/선 연산자, 글: Tj 등, 그림: Do/BI, 셰이딩: sh)의 번호.</summary>
    public int OpIndex { get; init; }
    /// <summary>객체를 이루는 첫 연산자 번호(경로: 첫 m/re 등. 그 밖에는 OpIndex와 같다).</summary>
    public int FirstOpIndex { get; init; }
    /// <summary>그리는 시점의 CTM(객체 좌표 → 쪽 사용자 공간).</summary>
    public Mat Ctm { get; init; }
    /// <summary>경로 만드는 중에 W/W*(클립)가 있었는가.</summary>
    public bool HasOwnClip { get; init; }
    /// <summary>쪽 공간 경계 사각형(경로: 점 모음, 그림/폼: 단위 정사각형/BBox 변환). 모르면 null(셰이딩·글).</summary>
    public Box? Bounds { get; init; }
    /// <summary>글 객체의 글 원점(쪽 공간). 위치를 모르면 null.</summary>
    public (double X, double Y)? Origin { get; init; }
    /// <summary>경로의 선 굵기(쪽 공간 배율 적용 후). 칠하기만 하면 0.</summary>
    public double StrokeWidth { get; init; }
    /// <summary>글 객체의 글꼴 크기(쪽 공간 배율 적용 후, pt). 글이 아니면 0.</summary>
    public double DeviceFontSize { get; init; }
    public TextSnapshot? Text { get; init; }
    /// <summary>Do 연산자가 가리킨 XObject 이름(해당 없으면 null).</summary>
    public string? XObjectName { get; init; }
}

public sealed class Segmentation
{
    public required IReadOnlyList<PageObject> Objects { get; init; }
    public string? Failure { get; init; }
    public bool Ok => Failure is null;
}

/// <summary>
/// 연산자 열 → 객체 목록. 분할 규칙은 PDFium의 객체 목록과 맞춘 V16 규칙이다:
/// 빈 글은 객체가 아니고, 경로는 점 2개 이상(또는 닫힌 한 점 경로)일 때만 객체이며 `n`은 객체가 아니다.
/// </summary>
public static class ContentSegmenter
{
    private sealed class GState
    {
        public Mat Ctm = Mat.Identity;
        public double Tc, Tw, TL, Ts, FontSize, LineWidth = 1, Tz = 100;
        public int Tr, FontOp = -1;
        public int[] Fill = [], Stroke = [], Misc = [];
        public GState Clone() => (GState)MemberwiseClone();   // 배열은 바꾸지 않고 새로 만들어 갈아 끼우므로 얕은 복사로 충분
    }

    private static readonly HashSet<string> PathPaint = ["S", "s", "f", "F", "f*", "B", "B*", "b", "b*"];
    private static readonly HashSet<string> StrokePaint = ["S", "s", "B", "B*", "b", "b*"];

    public static Segmentation Segment(ParsedContent pc, Func<string, XObjectInfo?>? xobjects = null)
    {
        var objects = new List<PageObject>();
        if (!pc.Ok) return new Segmentation { Objects = objects, Failure = "parse: " + pc.Failure };
        var ops = pc.Ops;
        var g = new GState();
        var stack = new Stack<GState>();

        // 경로
        int pathFirst = -1, points = 0; bool closedDot = false, pathClip = false, boxOk = true;
        double minX = 0, minY = 0, maxX = 0, maxY = 0; bool haveBox = false;
        // 글
        bool inText = false, tmKnown = false, textDirty = false;
        int btOp = -1;
        Mat tm = Mat.Identity, tlm = Mat.Identity;
        var textInBt = new List<TextSnapshot>();

        void AddPt(double x, double y)
        {
            var (px, py) = g.Ctm.Apply(x, y);
            if (!haveBox) { minX = maxX = px; minY = maxY = py; haveBox = true; }
            else { minX = Math.Min(minX, px); maxX = Math.Max(maxX, px); minY = Math.Min(minY, py); maxY = Math.Max(maxY, py); }
        }
        void ResetPath() { pathFirst = -1; points = 0; closedDot = false; pathClip = false; boxOk = true; haveBox = false; }

        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            switch (op.Name)
            {
                case "q": stack.Push(g.Clone()); if (inText) textDirty = true; break;
                case "Q": if (stack.Count > 0) g = stack.Pop(); if (inText) textDirty = true; break;
                case "cm":
                {
                    if (!Nums(op, 6, out var n)) return Fail(objects, "bad cm", i);
                    g.Ctm = new Mat(n[0], n[1], n[2], n[3], n[4], n[5]).Then(g.Ctm);
                    if (inText) textDirty = true;
                    break;
                }
                case "w": if (Nums(op, 1, out var lw)) g.LineWidth = lw[0]; SetMisc(g, ops, i); break;
                case "J": case "j": case "M": case "d": case "ri": case "i": case "gs": SetMisc(g, ops, i); break;
                case "g": case "rg": case "k": case "cs": g.Fill = [i]; break;
                case "G": case "RG": case "K": case "CS": g.Stroke = [i]; break;
                case "sc": case "scn": g.Fill = AppendColor(g.Fill, ops, i, "sc", "scn"); break;
                case "SC": case "SCN": g.Stroke = AppendColor(g.Stroke, ops, i, "SC", "SCN"); break;

                // --- 경로 ---
                case "m": case "l":
                    if (pathFirst < 0) pathFirst = i;
                    points++;
                    if (Nums(op, 2, out var p2)) AddPt(p2[0], p2[1]); else boxOk = false;
                    break;
                case "c":
                    if (pathFirst < 0) pathFirst = i;
                    points++;
                    if (Nums(op, 6, out var p6)) { AddPt(p6[0], p6[1]); AddPt(p6[2], p6[3]); AddPt(p6[4], p6[5]); } else boxOk = false;
                    break;
                case "v": case "y":
                    if (pathFirst < 0) pathFirst = i;
                    points++;
                    if (Nums(op, 4, out var p4)) { AddPt(p4[0], p4[1]); AddPt(p4[2], p4[3]); } else boxOk = false;
                    break;
                case "re":
                    if (pathFirst < 0) pathFirst = i;
                    points += 4;
                    if (Nums(op, 4, out var r))
                    { AddPt(r[0], r[1]); AddPt(r[0] + r[2], r[1]); AddPt(r[0], r[1] + r[3]); AddPt(r[0] + r[2], r[1] + r[3]); }
                    else boxOk = false;
                    break;
                case "h":
                    if (pathFirst < 0) pathFirst = i;
                    if (points == 1) closedDot = true;
                    break;
                case "W": case "W*": pathClip = true; break;
                case "n": ResetPath(); break;
                default:
                    if (PathPaint.Contains(op.Name))
                    {
                        if (points > 1 || (points == 1 && (closedDot || op.Name is "s" or "b" or "b*")))
                        {
                            double scale = Math.Sqrt(Math.Abs(g.Ctm.Determinant));
                            objects.Add(new PageObject
                            {
                                Kind = ObjectKind.Path, OpIndex = i, FirstOpIndex = pathFirst, Ctm = g.Ctm, HasOwnClip = pathClip,
                                Bounds = boxOk && haveBox ? new Box(minX, minY, maxX, maxY) : null,
                                StrokeWidth = StrokePaint.Contains(op.Name) ? g.LineWidth * scale : 0,
                            });
                        }
                        ResetPath();
                    }
                    break;

                // --- 글 ---
                case "BT": inText = true; tmKnown = true; textDirty = false; tm = tlm = Mat.Identity; btOp = i; break;
                case "ET":
                    foreach (var s in textInBt) if (s.BtOp == btOp) s.EtOp = i;
                    textInBt.Clear();
                    inText = false; tmKnown = false;
                    break;
                case "Tc": if (Nums(op, 1, out var a1)) g.Tc = a1[0]; break;
                case "Tw": if (Nums(op, 1, out var a2)) g.Tw = a2[0]; break;
                case "Tz": if (Nums(op, 1, out var a3)) g.Tz = a3[0]; break;
                case "TL": if (Nums(op, 1, out var a4)) g.TL = a4[0]; break;
                case "Ts": if (Nums(op, 1, out var a5)) g.Ts = a5[0]; break;
                case "Tr": if (Nums(op, 1, out var a6)) g.Tr = (int)a6[0]; break;
                case "Tf": if (Nums(op, 1, out var a7)) g.FontSize = a7[0]; g.FontOp = i; break;
                case "Tm":
                    if (!Nums(op, 6, out var m6)) return Fail(objects, "bad Tm", i);
                    tm = tlm = new Mat(m6[0], m6[1], m6[2], m6[3], m6[4], m6[5]); tmKnown = true;
                    break;
                case "Td": case "TD":
                    if (!Nums(op, 2, out var t2)) return Fail(objects, "bad Td", i);
                    if (op.Name == "TD") g.TL = -t2[1];
                    tlm = Mat.Translate(t2[0], t2[1]).Then(tlm); tm = tlm; tmKnown = true;
                    break;
                case "T*": tlm = Mat.Translate(0, -g.TL).Then(tlm); tm = tlm; tmKnown = true; break;
                case "Tj": case "TJ": case "'": case "\"":
                {
                    if (op.Name is "'" or "\"")
                    {
                        if (op.Name == "\"" && op.Operands.Count >= 3 && op.Operands[0].Kind == OperandKind.Number && op.Operands[1].Kind == OperandKind.Number) { g.Tw = op.Operands[0].Number; g.Tc = op.Operands[1].Number; }
                        tlm = Mat.Translate(0, -g.TL).Then(tlm); tm = tlm; tmKnown = true;
                    }
                    if (HasGlyphs(op))
                    {
                        var snap = new TextSnapshot
                        {
                            Tm = tm, Tc = g.Tc, Tw = g.Tw, Tz = g.Tz, TL = g.TL, Ts = g.Ts, Tr = g.Tr, FontOp = g.FontOp,
                            ReplayOps = [.. g.Misc, .. g.Fill, .. g.Stroke], BtOp = btOp,
                            Movable = inText && tmKnown && !textDirty && g.FontOp >= 0,
                        };
                        if (inText) textInBt.Add(snap);
                        (double, double)? origin = null;
                        if (tmKnown) { var (ox, oy) = tm.Then(g.Ctm).Apply(0, g.Ts); origin = (ox, oy); }
                        objects.Add(new PageObject { Kind = ObjectKind.Text, OpIndex = i, FirstOpIndex = i, Ctm = g.Ctm, Origin = origin, Text = snap, DeviceFontSize = tmKnown ? g.FontSize * Math.Sqrt(Math.Abs(tm.Then(g.Ctm).Determinant)) : 0 });
                    }
                    tmKnown = false;   // 글자 폭을 모르므로 보인 뒤의 Tm은 모른다(다음 Tm/Td/T*가 다시 알려 준다)
                    break;
                }

                // --- 그림·셰이딩 ---
                case "sh":
                    objects.Add(new PageObject { Kind = ObjectKind.Shading, OpIndex = i, FirstOpIndex = i, Ctm = g.Ctm });
                    break;
                case "BI":
                    objects.Add(new PageObject { Kind = ObjectKind.Image, OpIndex = i, FirstOpIndex = i, Ctm = g.Ctm, Bounds = UnitBox(g.Ctm) });
                    break;
                case "Do":
                {
                    string? name = op.Operands.Count > 0 && op.Operands[^1].Kind == OperandKind.Name ? op.Operands[^1].Text : null;
                    var info = name is null ? null : xobjects?.Invoke(name);
                    if (info is null) break;
                    Box? b;
                    if (info.Kind == ObjectKind.Image) b = UnitBox(g.Ctm);
                    else if (info.BBox is { } bb) b = BoxOf(info.FormMatrix.GetValueOrDefault(Mat.Identity).Then(g.Ctm), bb.L, bb.B, bb.R, bb.T);
                    else b = null;
                    objects.Add(new PageObject { Kind = info.Kind, OpIndex = i, FirstOpIndex = i, Ctm = g.Ctm, Bounds = b, XObjectName = name });
                    break;
                }
            }
        }
        return new Segmentation { Objects = objects };
    }

    private static Segmentation Fail(List<PageObject> objs, string why, int op) =>
        new() { Objects = objs, Failure = $"{why} at op {op}" };

    private static Box UnitBox(Mat ctm) => BoxOf(ctm, 0, 0, 1, 1);

    private static Box BoxOf(Mat m, double l, double b, double r, double t)
    {
        var pts = new[] { m.Apply(l, b), m.Apply(r, b), m.Apply(l, t), m.Apply(r, t) };
        return new Box(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
    }

    /// <summary>마지막 n개 피연산자가 모두 숫자여야 한다.</summary>
    internal static bool Nums(ContentOp op, int n, out double[] v)
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

    private static bool HasGlyphs(ContentOp op)
    {
        static bool Any(Operand o) => o.Kind switch
        {
            OperandKind.String => o.Bytes.Length > 0,
            OperandKind.Array => o.Items.Any(Any),
            _ => false,
        };
        return op.Operands.Any(Any);
    }

    private static void SetMisc(GState g, List<ContentOp> ops, int idx)
    {
        string name = ops[idx].Name;
        var list = new List<int>(g.Misc.Length + 1);
        foreach (int k in g.Misc) if (name == "gs" || ops[k].Name != name) list.Add(k);
        list.Add(idx);
        if (list.Count > 32) list.RemoveAt(0);
        g.Misc = list.ToArray();
    }

    private static int[] AppendColor(int[] cur, List<ContentOp> ops, int idx, string a, string b)
    {
        if (cur.Length > 0 && ops[cur[^1]].Name == a || cur.Length > 0 && ops[cur[^1]].Name == b)
        {
            var c = (int[])cur.Clone(); c[^1] = idx; return c;
        }
        return [.. cur, idx];
    }
}
