using System.Text.RegularExpressions;
using Pdf.Native;

namespace PdfEditor.TextEdit.Tests;

// M5 Task 1 (V17~V21) 탐침 구현. 정식 구현(Task 2~4)이 아니라 "이 알고리즘이 통하는가"를 재는 시험용이다.
// 좌표는 글 방향 기준 로컬 좌표: u = 글이 흐르는 방향, v = u를 반시계로 90° 돌린 방향("위"). 아래 줄은 v가 더 작다.

/// <summary>PDFium 글 객체 하나(보이는 글만). Obj = PDFium 객체 번호.</summary>
internal sealed record Run(int Obj, string Text, double U0, double U1, double V, double Size, int Angle, string Font, int Color,
    double OX, double OY, double FontSizeRaw, double Scale)
{
    public string Trim => Text.Trim();
}

internal sealed record Obst(double L, double B, double R, double T, bool IsImage);

internal sealed class PageData
{
    public double W, H;
    public List<Run> Runs = [];
    public List<Obst> Obstacles = [];
    public int TextObjects, Hidden, BadAngle, Forms, Images, Paths, TotalObjects;
    public double ReadMs;
    public List<PdfEditor.ContentStream.ObjectKind> Kinds = [];
}

/// <summary>줄 조립 결과: 같은 기준선에서 1 em 안으로 이어진 글 객체들(칸 하나의 한 줄).</summary>
internal sealed class Seg
{
    public List<Run> Runs = [];
    public int Angle;
    public double U0, U1, V, Size;
    public string Font = "";
    public int Color;
    public bool Mixed;
    public string Text = "";
    public int Dups;
    public bool TableRow;
    public double Width => U1 - U0;
    public override string ToString() => $"[{Angle}° u{U0:0.#}-{U1:0.#} v{V:0.#} s{Size:0.#}] {Text}";
}

internal enum Align { None, Left, Justified, Center, Right }

internal sealed class Group
{
    public List<Seg> Lines = [];
    public Align Align;
    public double Confidence;
    public string Reason = "";
}

internal static class ParagraphProbe
{
    // ----------------------------------------------------------------- 읽기 (PDFium 스레드)
    internal static PageData ReadPdfium(PdfiumThread t, string path, int pageIndex)
    {
        return t.InvokeAsync(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var d = Pdf.Native.PdfDocument.Open(t, path, null);
            using var p = d.LoadPage(pageIndex);
            using var tp = p.LoadText();
            var pd = new PageData { W = p.SizePt.Width, H = p.SizePt.Height, TotalObjects = p.ObjectCount };
            for (int i = 0; i < pd.TotalObjects; i++)
            {
                var o = p.GetObject(i);
                pd.Kinds.Add((PdfEditor.ContentStream.ObjectKind)(int)o.Type);
                switch (o.Type)
                {
                    case PdfPageObjectType.Text:
                    {
                        pd.TextObjects++;
                        var text = o.GetText(tp);
                        if (string.IsNullOrWhiteSpace(text)) break;
                        var r = ReadRun(i, o, text);
                        if (r.Bad) { pd.BadAngle++; break; }
                        if (!r.Visible) { pd.Hidden++; break; }
                        pd.Runs.Add(r.Run!);
                        break;
                    }
                    case PdfPageObjectType.Path:
                        pd.Paths++;
                        AddPathObstacles(o, pd);
                        break;
                    case PdfPageObjectType.Image:
                    {
                        pd.Images++;
                        var b = o.Bounds;
                        pd.Obstacles.Add(new Obst(b.Left, b.Bottom, b.Right, b.Top, true));
                        break;
                    }
                    case PdfPageObjectType.Form: pd.Forms++; break;
                }
            }
            pd.ReadMs = sw.Elapsed.TotalMilliseconds;
            return pd;
        }).GetAwaiter().GetResult();
    }

    internal static (bool Bad, bool Visible, Run? Run) ReadRun(int index, PdfPageObject o, string text)
    {
        var m = o.Matrix;
        double ang = Math.Atan2(m.B, m.A) * 180 / Math.PI;
        int a = (int)Math.Round(((ang % 360) + 360) % 360 / 90.0) % 4 * 90;
        double diff = Math.Abs(((ang - a + 540) % 360) - 180);
        if (diff > 3) return (true, false, null);
        int tr = o.TextRenderMode;
        var fill = o.FillColor;
        bool visible = tr != 3 && !(tr == 0 && fill is { A: 0 });
        if (!visible) return (false, false, null);
        double rad = a * Math.PI / 180, ux = Math.Cos(rad), uy = Math.Sin(rad), vx = -uy, vy = ux;
        var b = o.Bounds;
        var xs = new[] { b.Left, b.Right }; var ys = new[] { b.Bottom, b.Top };
        double u0 = double.MaxValue, u1 = double.MinValue;
        foreach (var x in xs) foreach (var y in ys) { double u = x * ux + y * uy; u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); }
        double scale = Math.Sqrt((double)m.C * m.C + (double)m.D * m.D);
        double size = o.FontSize * scale;
        if (size <= 0.01) return (true, false, null);
        var f = o.Font;
        int color = fill is { } c ? (c.R << 16 | c.G << 8 | c.B) : 0;
        return (false, true, new Run(index, text, u0, u1, m.E * vx + m.F * vy, size, a, $"{f?.BaseName}|{f?.Embedded}", color, m.E, m.F, o.FontSize, scale));
    }

    internal static void AddPathObstacles(PdfPageObject o, PageData pd)
    {
        var b = o.Bounds;
        double w = b.Width, h = b.Height;
        if (w * h >= 0.9 * pd.W * pd.H) return;                      // 쪽 전체 테두리·배경은 장애물이 아니다
        var g = (w > 2.5 && h > 2.5) ? o.ReadPathGeometry(512) : null;
        if (g is { Stroked: true } || (g is null && (w > 2.5 && h > 2.5) && o.StrokeWidth > 0))
        {
            if (g is null) { pd.Obstacles.Add(new Obst(b.Left, b.Bottom, b.Right, b.Top, false)); return; }   // 너무 복잡한 선 그림: 경계 상자
            foreach (var sp in g.Subpaths)
            {
                var pts = sp.Points;
                int n = pts.Length;
                for (int i = 0; i + 1 < n + (sp.Closed ? 1 : 0); i++)
                {
                    var p0 = pts[i]; var p1 = pts[(i + 1) % n];
                    pd.Obstacles.Add(new Obst(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y), false));
                }
            }
            return;
        }
        if (Math.Min(w, h) <= 2.5) pd.Obstacles.Add(new Obst(b.Left, b.Bottom, b.Right, b.Top, false));   // 칠한 가는 막대 = 선
    }

    // ----------------------------------------------------------------- 줄 조립 (C3 ①)
    internal static List<Seg> Assemble(IReadOnlyList<Run> runs, IReadOnlyList<Obst>? obstacles = null)
    {
        var segs = new List<Seg>();
        foreach (var angleGroup in runs.GroupBy(r => r.Angle))
        {
            // 겹쳐 찍은 같은 글 합치기
            var list = new List<Run>();
            var dups = new Dictionary<Run, int>();
            foreach (var r in angleGroup.OrderBy(r => r.Obj))
            {
                var d = list.FirstOrDefault(x => x.Trim == r.Trim && Math.Abs(x.U0 - r.U0) <= 0.12 * r.Size && Math.Abs(x.V - r.V) <= 0.12 * r.Size);
                if (d is null) list.Add(r); else dups[d] = dups.GetValueOrDefault(d) + 1;
            }
            // 기준선별 묶기
            var clusters = new List<List<Run>>();
            foreach (var r in list.OrderByDescending(r => r.V))
            {
                var c = clusters.FirstOrDefault(c => Math.Abs(c[0].V - r.V) <= 0.15 * Math.Min(c[0].Size, r.Size));
                if (c is null) clusters.Add([r]); else c.Add(r);
            }
            foreach (var c in clusters)
            {
                var sorted = c.OrderBy(r => r.U0).ToList();
                Seg? cur = null;
                foreach (var r in sorted)
                {
                    bool marker = cur is { Runs.Count: 1 } && cur.Runs[0].Trim.Length <= 3 && Bullet.IsMatch(cur.Runs[0].Trim + " ");
                    double limit = (marker ? 3.0 : 1.0) * (cur?.Runs[0].Size ?? 0);
                    if (cur is null || r.U0 - cur.U1 > limit || VRule(cur.U1, r.U0, cur.Runs[0].V, cur.Runs[0].Size, r.Angle, obstacles ?? [])) { cur = new Seg { Angle = r.Angle }; segs.Add(cur); }
                    cur.Runs.Add(r);
                    cur.U1 = cur.Runs.Count == 1 ? r.U1 : Math.Max(cur.U1, r.U1);
                    if (cur.Runs.Count == 1) cur.U0 = r.U0;
                    cur.Dups += dups.GetValueOrDefault(r);
                }
            }
        }
        foreach (var s in segs) Finish(s);
        MarkTableRows(segs, obstacles ?? []);
        return segs;
    }

    private static bool VRule(double uFrom, double uTo, double v, double size, int angle, IReadOnlyList<Obst> obstacles) =>
        VerticalRuleBetween(new Seg { Angle = angle, U0 = uFrom - 1, U1 = uFrom, V = v, Size = size }, new Seg { Angle = angle, U0 = uTo, U1 = uTo + 1, V = v, Size = size }, obstacles);

    private static void Finish(Seg s)
    {
        var rs = s.Runs;
        s.Size = rs.Select(r => r.Size).OrderBy(x => x).ElementAt(rs.Count / 2);
        s.V = rs.Select(r => r.V).OrderBy(x => x).ElementAt(rs.Count / 2);
        s.Font = rs[0].Font; s.Color = rs[0].Color;
        s.Mixed = rs.Any(r => r.Font != s.Font || r.Color != s.Color || Math.Abs(r.Size / s.Size - 1) > 0.03);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < rs.Count; i++)
        {
            if (i > 0 && rs[i].U0 - rs[i - 1].U1 > 0.25 * s.Size) sb.Append(' ');
            sb.Append(rs[i].Trim);
        }
        s.Text = sb.ToString();
    }

    /// <summary>같은 기준선에 다른 칸이 있고 두 칸의 "세로줄 글 폭 중앙값"이 모두 12 em 이하(짧은 칸)이거나 사이에 세로 선이 있으면 표의 행으로 본다.</summary>
    private static void MarkTableRows(List<Seg> segs, IReadOnlyList<Obst> obstacles)
    {
        foreach (var g in segs.GroupBy(s => s.Angle))
        {
            var l = g.ToList();
            double ColLen(Seg x)
            {
                var col = l.Where(y => !ReferenceEquals(y, x) && Math.Abs(y.U0 - x.U0) <= 0.5 * x.Size && Math.Abs(y.Size / x.Size - 1) <= 0.03 && y.Font == x.Font).Select(y => y.Width / y.Size).Append(x.Width / x.Size).ToList();
                return col.Count >= 3 ? Median(col) : x.Width / x.Size;
            }
            foreach (var s in l)
                foreach (var o in l)
                {
                    if (ReferenceEquals(s, o) || Math.Abs(o.V - s.V) > 0.5 * Math.Min(s.Size, o.Size)) continue;
                    if ((ColLen(s) <= 12 && ColLen(o) <= 12) || VerticalRuleBetween(s, o, obstacles)) { s.TableRow = true; break; }
                }
        }
    }

    /// <summary>두 칸 글 사이(u)에 같은 높이대를 가로지르는 세로 선이 있나(칸막이 선).</summary>
    private static bool VerticalRuleBetween(Seg a, Seg b, IReadOnlyList<Obst> obstacles)
    {
        var left = a.U0 < b.U0 ? a : b; var right = ReferenceEquals(left, a) ? b : a;
        double ang = a.Angle * Math.PI / 180, ux = Math.Cos(ang), uy = Math.Sin(ang), vx = -uy, vy = ux;
        foreach (var o in obstacles)
        {
            if (o.IsImage) continue;
            double ou0 = double.MaxValue, ou1 = double.MinValue, ov0 = double.MaxValue, ov1 = double.MinValue;
            foreach (var x in new[] { o.L, o.R }) foreach (var y in new[] { o.B, o.T })
            { double u = x * ux + y * uy, v = x * vx + y * vy; ou0 = Math.Min(ou0, u); ou1 = Math.Max(ou1, u); ov0 = Math.Min(ov0, v); ov1 = Math.Max(ov1, v); }
            if (ou1 - ou0 > 2.5 || ov1 - ov0 < left.Size) continue;       // 가는 세로 선만
            if (ou0 < left.U1 || ou0 > right.U0) continue;
            if (ov0 <= left.V && ov1 >= left.V + 0.7 * left.Size) return true;
        }
        return false;
    }

    // ----------------------------------------------------------------- 문단 추정 (C3 ②)
    private static readonly Regex Bullet = new(@"^\s*(?:[•·●○■□▪◦*\-–—]|\d{1,3}[.)]|\(\d{1,3}\)|[A-Za-z][.)])\s", RegexOptions.Compiled);
    internal static bool IsBulletStart(Seg s) => Bullet.IsMatch(s.Text + " ");

    internal sealed class Page(List<Seg> segs, IReadOnlyList<Obst> obstacles)
    {
        public List<Seg> Segs = segs;
        public IReadOnlyList<Obst> Obstacles = obstacles;
    }

    internal static Group Detect(Page pg, Seg seed)
    {
        var g = new List<Seg> { seed };
        if (!seed.Mixed && !seed.TableRow)
        {
            while (true)
            {
                var t = Neighbor(pg, g[^1], down: true);
                if (t is null || !CanAdd(pg, g, t, down: true)) break;
                g.Add(t);
            }
            while (true)
            {
                var u = Neighbor(pg, g[0], down: false);
                if (u is null || !CanAdd(pg, g, u, down: false)) break;
                g.Insert(0, u);
            }
        }
        var grp = new Group { Lines = g };
        Score(pg, grp);
        return grp;
    }

    internal static Seg? Neighbor(Page pg, Seg s, bool down)
    {
        Seg? best = null; double bestD = double.MaxValue; int tie = 0;
        foreach (var t in pg.Segs)
        {
            if (ReferenceEquals(t, s) || t.Angle != s.Angle) continue;
            double dv = down ? s.V - t.V : t.V - s.V;
            if (dv < 0.3 * s.Size) continue;
            double ov = Math.Min(s.U1, t.U1) - Math.Max(s.U0, t.U0);
            if (ov < 0.3 * Math.Min(s.Width, t.Width) || ov <= 0) continue;
            if (dv < bestD - 0.3 * s.Size) { best = t; bestD = dv; tie = 0; }
            else if (Math.Abs(dv - bestD) <= 0.3 * s.Size) tie++;
        }
        return tie > 0 ? null : best;
    }

    private static bool SameStyle(Seg a, Seg b) =>
        !a.Mixed && !b.Mixed && a.Font == b.Font && a.Color == b.Color && Math.Abs(a.Size / b.Size - 1) <= 0.03;

    private static bool CanAdd(Page pg, List<Seg> g, Seg t, bool down) => WhyNot(pg, g, t, down) is null;

    /// <summary>t를 그룹에 더하지 못하는 이유(더할 수 있으면 null).</summary>
    internal static string? WhyNot(Page pg, List<Seg> g, Seg t, bool down)
    {
        var s = down ? g[^1] : g[0];
        if (!SameStyle(s, t)) return "style";
        if (t.TableRow || s.TableRow) return "tablerow";
        double pitch = Math.Abs(s.V - t.V), em = s.Size;
        if (pitch < 1.0 * em || pitch > 2.2 * em) return $"pitch {pitch / em:0.00}em";
        if (g.Count >= 2)
        {
            double p0 = Median(Pitches(g));
            if (Math.Abs(pitch - p0) > 0.25 * p0) return "pitch inconsistent";
        }
        if (ObstacleBetween(pg, s, t)) return "obstacle";
        if (down && IsBulletStart(t)) return "bullet starts";
        bool tBullet = !down && IsBulletStart(t);
        if (!down && IsBulletStart(s) && g.Count >= 1) return "bullet is first";
        var cand = down ? g.Append(t).ToList() : g.Prepend(t).ToList();
        var cls = Classify(cand);
        if (cls == Align.None) return "alignment";
        if (cls is Align.Left or Align.Justified)
        {
            if (down)
            {
                double lmain = g.Count >= 2 ? Median(g.Skip(1).Select(x => x.U0)) : s.U0;
                if (!IsBulletStart(g[0]) && t.U0 - lmain > 0.8 * em) return "indented line starts paragraph";
            }
            else if (!tBullet)
            {
                double lmain = g.Count >= 2 ? Median(g.Skip(1).Select(x => x.U0)) : t.U0;
                if (g.Count >= 2 && g[0].U0 - lmain > 0.8 * em) return "first line is indented";
                if (g.Count == 1 && s.U0 - t.U0 > 0.8 * em) return "first line is indented";
            }
        }
        // 줄 폭이 열 폭의 절반 이하인 짧은 줄은 문단의 끝이다(이어 쓰는 줄바꿈은 다음 낱말이 안 들어갈 때만 생기므로 열 폭 근처까지 찬다)
        if (cls is Align.Left or Align.Justified)
        {
            double colR = pg.Segs.Where(x => x.Angle == s.Angle && !x.Mixed && SameStyle(x, s) && Math.Abs(x.U0 - s.U0) <= 4 * em).Max(x => x.U1);
            double colW = colR - Math.Min(s.U0, t.U0);
            bool Short(Seg x) => colW > 12 * em && x.Width <= 0.5 * colW;
            if (down && Short(s)) return "short line ends the paragraph";
            if (!down && Short(t)) return "upper line is short (ends previous paragraph)";
        }
        double tol = 0.5 * em;
        if (down && g.Count >= 3)
        {
            var body = g.Take(g.Count - 1).Select(x => x.U1).ToList();
            if (body.Max() - body.Min() <= tol && g[^1].U1 < Median(body) - 2.5 * em) return "short last line";
        }
        if (!down && g.Count >= 2)
        {
            var body = g.Take(g.Count - 1).Select(x => x.U1).ToList();
            double rfull = g.Count == 2 ? g[0].U1 : Median(body);
            if ((g.Count == 2 || body.Max() - body.Min() <= tol) && t.U1 < rfull - 2.5 * em) return "upper line short";
        }
        return null;
    }

    internal static Align Classify(List<Seg> g)
    {
        int n = g.Count;
        if (n == 1) return Align.Left;
        double em = g.Average(x => x.Size), tol = 0.5 * em;
        double lmain = Median(g.Skip(1).Select(x => x.U0));
        bool leftOk = g.Skip(1).All(x => Math.Abs(x.U0 - lmain) <= tol) && Math.Abs(g[0].U0 - lmain) <= 4 * em;
        if (leftOk)
        {
            var body = g.Take(n - 1).Select(x => x.U1).ToList();
            return n >= 3 && body.Max() - body.Min() <= tol ? Align.Justified : Align.Left;
        }
        double c0 = Median(g.Select(x => (x.U0 + x.U1) / 2));
        if (g.All(x => Math.Abs((x.U0 + x.U1) / 2 - c0) <= tol * 1.2)) return Align.Center;
        double r0 = Median(g.Select(x => x.U1));
        if (g.All(x => Math.Abs(x.U1 - r0) <= tol)) return Align.Right;
        return Align.None;
    }

    private static bool ObstacleBetween(Page pg, Seg a, Seg b)
    {
        var upper = a.V > b.V ? a : b; var lower = ReferenceEquals(upper, a) ? b : a;
        double vHi = upper.V - 0.25 * upper.Size, vLo = lower.V + 0.8 * lower.Size;
        if (vHi <= vLo) return false;
        double ang = a.Angle * Math.PI / 180, ux = Math.Cos(ang), uy = Math.Sin(ang), vx = -uy, vy = ux;
        foreach (var o in pg.Obstacles)
        {
            double ou0 = double.MaxValue, ou1 = double.MinValue, ov0 = double.MaxValue, ov1 = double.MinValue;
            foreach (var x in new[] { o.L, o.R }) foreach (var y in new[] { o.B, o.T })
            { double u = x * ux + y * uy, v = x * vx + y * vy; ou0 = Math.Min(ou0, u); ou1 = Math.Max(ou1, u); ov0 = Math.Min(ov0, v); ov1 = Math.Max(ov1, v); }
            if (ov1 < vLo || ov0 > vHi) continue;                       // 줄 사이 띠와 세로로 안 겹침
            double lo = Math.Min(a.U0, b.U0), hi = Math.Max(a.U1, b.U1);
            if (ou1 < lo || ou0 > hi) continue;                         // 가로로 안 겹침
            if (o.IsImage && ou0 <= lo && ou1 >= hi && ov0 <= lower.V - 0.3 * lower.Size && ov1 >= upper.V + 0.8 * upper.Size) continue;   // 두 줄을 다 덮은 배경 그림
            return true;
        }
        return false;
    }

    // ----------------------------------------------------------------- 신뢰도 (C4)
    private static void Score(Page pg, Group grp)
    {
        var g = grp.Lines;
        int n = g.Count;
        grp.Align = Classify(g);
        if (n == 1) { grp.Confidence = 0; grp.Reason = "single"; return; }
        double c = n switch { 2 => 0.55, 3 => 0.72, 4 => 0.80, _ => 0.85 };
        var pit = Pitches(g);
        double mean = pit.Average(), cv = n >= 3 ? Math.Sqrt(pit.Average(p => (p - mean) * (p - mean))) / mean : 0;
        if (n >= 3) c += cv < 0.02 ? 0.07 : cv < 0.06 ? 0.03 : -0.10;
        double em = g[0].Size, ratio = mean / em;
        if (ratio < 1.1 || ratio > 1.9) c -= 0.10;
        if (n == 2 && ratio > 1.6) c -= 0.15;
        if (grp.Align == Align.Justified) c += 0.08;
        if (grp.Align is Align.Left or Align.Justified)
        {
            double lmain = Median(g.Skip(1).Select(x => x.U0));
            double rstyle = pg.Segs.Where(x => x.Angle == g[0].Angle && !x.Mixed && SameStyle(x, g[0]) && Math.Abs(x.U0 - lmain) <= 4 * em).Max(x => x.U1);
            double fullFrac = g.Take(n - 1).Count(x => x.U1 >= rstyle - 1.2 * em) / (double)(n - 1);
            c += 0.10 * fullFrac;
        }
        double avgLen = g.Average(x => x.Text.Length), avgEm = g.Average(x => x.Width / x.Size);
        if (avgEm < 10) c -= 0.25; else if (avgEm < 15) c -= 0.10;      // 아주 짧은 줄(범례·라벨 목록)은 문단일 가능성이 낮다
        if (g.All(x => x.Text.Split(' ').Length <= 3)) c -= 0.10;
        // 목록 성격: 이음 줄이 대문자로 시작하는 라틴 글이 대부분이면 항목 나열일 가능성이 높다(전부 대문자 글은 판단 불가). 가운데 맞춤 4줄 이상도 문단보다 목록.
        var cont = g.Skip(1).Select(x => x.Text.TrimStart()).Where(t => t.Length > 0 && char.IsLetter(t[0]) && t[0] < 0x250).ToList();
        var letters = g.SelectMany(x => x.Text).Where(ch => ch < 0x250 && char.IsLetter(ch)).ToList();
        bool allCaps = letters.Count > 0 && letters.Count(char.IsUpper) >= 0.8 * letters.Count;
        if (cont.Count >= 2 && !allCaps && cont.Count(t => char.IsUpper(t[0])) >= 0.6 * cont.Count) c -= 0.25;
        if (grp.Align == Align.Center && n >= 4) c -= 0.30;
        if (avgEm < 4) c = Math.Min(c, 0.3);
        grp.Confidence = Math.Clamp(c, 0, 1);
        grp.Reason = $"n={n} cv={cv:0.00} ratio={ratio:0.00} align={grp.Align} avgLen={avgLen:0} avgEm={avgEm:0.#}";
    }

    internal static List<double> Pitches(List<Seg> g)
    {
        var l = new List<double>();
        for (int i = 0; i + 1 < g.Count; i++) l.Add(Math.Abs(g[i].V - g[i + 1].V));
        return l;
    }

    internal static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToList();
        return a.Count == 0 ? 0 : a.Count % 2 == 1 ? a[a.Count / 2] : (a[a.Count / 2 - 1] + a[a.Count / 2]) / 2;
    }

    /// <summary>읽는 순서(위→아래, 왼쪽→오른쪽)로 씨앗을 돌며 문단을 확정한다. 신뢰도가 문턱 미만이면 그 줄은 혼자(한 줄 편집으로 되돌림).</summary>
    internal static List<Group> Partition(Page pg, double threshold, Dictionary<Seg, Group>? cache = null)
    {
        var claimed = new HashSet<Seg>();
        var result = new List<Group>();
        foreach (var s in pg.Segs.OrderBy(s => s.Angle).ThenByDescending(s => s.V).ThenBy(s => s.U0))
        {
            if (claimed.Contains(s)) continue;
            Group grp;
            if (cache is not null && cache.TryGetValue(s, out var cg)) grp = cg;
            else { grp = Detect(pg, s); cache?.Add(s, grp); }
            if (grp.Lines.Count >= 2 && grp.Confidence >= threshold && grp.Lines.All(l => !claimed.Contains(l)))
            { foreach (var l in grp.Lines) claimed.Add(l); result.Add(grp); }
            else { claimed.Add(s); result.Add(new Group { Lines = [s], Align = Align.Left, Confidence = grp.Confidence, Reason = grp.Lines.Count >= 2 ? "low confidence: " + grp.Reason : grp.Reason }); }
        }
        return result;
    }
}
