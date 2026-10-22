using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;

namespace PdfEditor.TextEdit.Paragraphs;

/// <summary>
/// 줄 조립 결과: 같은 기준선에서 이어진 글 객체들(칸 하나의 한 줄). 좌표는 글 방향 기준 로컬 좌표 —
/// u = 글이 흐르는 방향, v = u를 반시계로 90° 돌린 방향("위"). 아래 줄은 v가 더 작다.
/// </summary>
public sealed class TextLine
{
    public required ImmutableArray<TextRun> Runs { get; init; }
    /// <summary>겹쳐 찍은 같은 글(굵게 흉내)의 원래 참조. 편집하면 이것도 함께 다룬다.</summary>
    public ImmutableArray<ObjectRef> Duplicates { get; init; } = [];
    public required PdfRectF Bounds { get; init; }
    public required int Angle { get; init; }
    public required double U0 { get; init; }
    public required double U1 { get; init; }
    /// <summary>기준선(로컬 v).</summary>
    public required double Baseline { get; init; }
    public required double Size { get; init; }
    public required string FontKey { get; init; }
    public required PdfColor Color { get; init; }
    public required double Tz { get; init; }
    /// <summary>한 줄 안에 글꼴·크기·색·가로 비율이 섞여 있다(묶으면 안 됨).</summary>
    public required bool Mixed { get; init; }
    public required string Text { get; init; }
    /// <summary>같은 기준선에 다른 칸이 있는 표의 행(표 칸 병합 오탐 방지).</summary>
    public bool TableRow { get; internal set; }

    public double Width => U1 - U0;
    public IEnumerable<ObjectRef> AllRefs => Runs.Select(r => r.Ref).Concat(Duplicates);
    public override string ToString() => $"[{Angle}° u{U0:0.#}-{U1:0.#} v{Baseline:0.#} s{Size:0.#}] {Text}";
}

internal static class Geo
{
    /// <summary>각도(0·90·180·270)에서 로컬 축 방향 (ux, uy, vx, vy).</summary>
    public static (double Ux, double Uy, double Vx, double Vy) Axes(int angle) => angle switch
    {
        0 => (1, 0, 0, 1),
        90 => (0, 1, -1, 0),
        180 => (-1, 0, 0, -1),
        _ => (0, -1, 1, 0),
    };

    public static (double U0, double U1, double V0, double V1) Project(PdfRectF r, int angle)
    {
        var (ux, uy, vx, vy) = Axes(angle);
        double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
        foreach (double x in new double[] { r.Left, r.Right })
            foreach (double y in new double[] { r.Bottom, r.Top })
            {
                double u = x * ux + y * uy, v = x * vx + y * vy;
                u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
            }
        return (u0, u1, v0, v1);
    }

    /// <summary>0/90/180/270으로 정규화. 그 밖의 각도는 -1.</summary>
    public static int NormalizeAngle(int deg)
    {
        int a = ((deg % 360) + 360) % 360;
        return a % 90 == 0 ? a : -1;
    }

    public static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToList();
        return a.Count == 0 ? 0 : a.Count % 2 == 1 ? a[a.Count / 2] : (a[a.Count / 2 - 1] + a[a.Count / 2]) / 2;
    }
}

public static class LineAssembler
{
    private static readonly Regex Bullet = new(@"^\s*(?:[•·●○■□▪◦*\-–—]|\d{1,3}[.)]|\(\d{1,3}\)|[A-Za-z][.)])\s", RegexOptions.Compiled);

    public static bool IsBulletStart(string text) => Bullet.IsMatch(text + " ");

    private sealed class LRun(TextRun run, int angle, double u0, double u1, double v)
    {
        public TextRun Run = run;
        public int Angle = angle;
        public double U0 = u0, U1 = u1, V = v;
        public double Size => Run.SizePt;
        public string Trim => Run.Text.Trim();
        public readonly List<ObjectRef> Dups = [];
    }

    /// <summary>
    /// 글 객체 → 줄. 보이지 않는 글·공백뿐인 글·0/90/180/270°가 아닌 글은 편집 대상이 아니라 버린다.
    /// <paramref name="obstacles"/>는 칸 사이 세로선(줄 끊기)과 표 행 판정에 쓴다. 결과는 각도 → 위에서 아래 → 왼쪽에서 오른쪽 순.
    /// </summary>
    public static ImmutableArray<TextLine> Assemble(IReadOnlyList<TextRun> runs, IReadOnlyList<Obstacle>? obstacles = null, ParagraphOptions? options = null)
    {
        var opt = options ?? new ParagraphOptions();
        var rules = (obstacles ?? []).Where(o => o.Kind is ObstacleKind.Line or ObstacleKind.Fill).ToList();
        var lines = new List<TextLine>();

        var usable = new List<LRun>();
        foreach (var r in runs)
        {
            if (!r.Visible || string.IsNullOrWhiteSpace(r.Text) || r.SizePt <= 0.01f) continue;
            int a = Geo.NormalizeAngle(r.AngleDeg);
            if (a < 0) continue;
            var (u0, u1, _, _) = Geo.Project(r.Bounds, a);
            var (_, _, vx, vy) = Geo.Axes(a);
            usable.Add(new LRun(r, a, u0, u1, r.Origin.X * vx + r.Origin.Y * vy));
        }

        foreach (var angleGroup in usable.GroupBy(r => r.Angle).OrderBy(g => g.Key))
        {
            int angle = angleGroup.Key;
            // 겹쳐 찍은 같은 글 합치기(객체 번호가 앞선 것을 대표로)
            var list = new List<LRun>();
            foreach (var r in angleGroup.OrderBy(r => r.Run.Ref.OriginalIndex).ThenBy(r => r.Run.Ref.InsertedId))
            {
                double tol = opt.DuplicateTolEm * r.Size;
                var d = list.FirstOrDefault(x => x.Trim == r.Trim && Math.Abs(x.U0 - r.U0) <= tol && Math.Abs(x.V - r.V) <= tol);
                if (d is null) list.Add(r); else d.Dups.Add(r.Run.Ref);
            }
            // 기준선별 묶기
            var clusters = new List<List<LRun>>();
            foreach (var r in list.OrderByDescending(r => r.V))
            {
                var c = clusters.FirstOrDefault(c => Math.Abs(c[0].V - r.V) <= opt.BaselineTolEm * Math.Min(c[0].Size, r.Size));
                if (c is null) clusters.Add([r]); else c.Add(r);
            }
            var segs = new List<List<LRun>>();
            foreach (var c in clusters)
            {
                List<LRun>? cur = null;
                double curU1 = 0;
                foreach (var r in c.OrderBy(r => r.U0))
                {
                    if (cur is not null)
                    {
                        bool marker = cur.Count == 1 && cur[0].Trim.Length <= opt.BulletMaxChars && IsBulletStart(cur[0].Trim);
                        double limit = (marker ? opt.BulletGapEm : opt.CellGapEm) * cur[0].Size;
                        if (r.U0 - curU1 > limit || VerticalRuleBetween(rules, angle, curU1, r.U0, cur[0].V, cur[0].Size, opt)) cur = null;
                    }
                    if (cur is null) { cur = []; segs.Add(cur); curU1 = r.U1; }
                    else curU1 = Math.Max(curU1, r.U1);
                    cur.Add(r);
                }
            }
            foreach (var s in segs) lines.Add(Finish(s, angle, opt));
        }

        MarkTableRows(lines, rules, opt);
        return [.. lines.OrderBy(l => l.Angle).ThenByDescending(l => l.Baseline).ThenBy(l => l.U0)];
    }

    private static TextLine Finish(List<LRun> rs, int angle, ParagraphOptions opt)
    {
        double size = Geo.Median(rs.Select(r => r.Size));
        double v = Geo.Median(rs.Select(r => r.V));
        var first = rs[0].Run;
        bool mixed = rs.Any(r => r.Run.FontKey != first.FontKey || r.Run.Color != first.Color
            || Math.Abs(r.Size / size - 1) > opt.SizeTol || Math.Abs(r.Run.Tz - first.Tz) > opt.TzTol);
        var sb = new StringBuilder();
        for (int i = 0; i < rs.Count; i++)
        {
            if (i > 0 && rs[i].U0 - rs[i - 1].U1 > opt.SpaceGapEm * size) sb.Append(' ');
            sb.Append(rs[i].Trim);
        }
        var bounds = rs[0].Run.Bounds;
        foreach (var r in rs.Skip(1)) bounds = bounds.Union(r.Run.Bounds);
        return new TextLine
        {
            Runs = [.. rs.Select(r => r.Run)],
            Duplicates = [.. rs.SelectMany(r => r.Dups)],
            Bounds = bounds,
            Angle = angle,
            U0 = rs.Min(r => r.U0),
            U1 = rs.Max(r => r.U1),
            Baseline = v,
            Size = size,
            FontKey = first.FontKey,
            Color = first.Color,
            Tz = first.Tz,
            Mixed = mixed,
            Text = sb.ToString(),
        };
    }

    /// <summary>두 칸 글 사이(u)에 같은 높이대를 가로지르는 가는 세로선이 있나(칸막이 선).</summary>
    private static bool VerticalRuleBetween(IReadOnlyList<Obstacle> rules, int angle, double leftU1, double rightU0, double v, double size, ParagraphOptions opt)
    {
        foreach (var o in rules)
        {
            var (ou0, ou1, ov0, ov1) = Geo.Project(o.Bounds, angle);
            if (ou1 - ou0 > opt.VerticalRuleMaxWidth || ov1 - ov0 < size) continue;   // 가는 세로 선만
            if (ou0 < leftU1 || ou0 > rightU0) continue;
            if (ov0 <= v && ov1 >= v + 0.7 * size) return true;
        }
        return false;
    }

    /// <summary>같은 기준선에 다른 칸이 있고 두 칸의 "같은 열 글 폭 중앙값"이 모두 12 em 이하(짧은 칸)이거나 사이에 세로선이 있으면 표의 행.</summary>
    private static void MarkTableRows(List<TextLine> lines, IReadOnlyList<Obstacle> rules, ParagraphOptions opt)
    {
        foreach (var g in lines.GroupBy(s => s.Angle))
        {
            var l = g.ToList();
            var colLen = new double[l.Count];
            for (int i = 0; i < l.Count; i++)
            {
                var x = l[i];
                var col = l.Where(y => !ReferenceEquals(y, x) && Math.Abs(y.U0 - x.U0) <= 0.5 * x.Size
                        && Math.Abs(y.Size / x.Size - 1) <= opt.SizeTol && y.FontKey == x.FontKey)
                    .Select(y => y.Width / y.Size).Append(x.Width / x.Size).ToList();
                colLen[i] = col.Count >= opt.TableColumnMinSamples ? Geo.Median(col) : x.Width / x.Size;
            }
            for (int i = 0; i < l.Count; i++)
                for (int j = 0; j < l.Count; j++)
                {
                    if (i == j) continue;
                    var s = l[i];
                    var o = l[j];
                    if (Math.Abs(o.Baseline - s.Baseline) > 0.5 * Math.Min(s.Size, o.Size)) continue;
                    var left = s.U0 < o.U0 ? s : o;
                    var right = ReferenceEquals(left, s) ? o : s;
                    if ((colLen[i] <= opt.TableColumnEm && colLen[j] <= opt.TableColumnEm)
                        || VerticalRuleBetween(rules, g.Key, left.U1, right.U0, left.Baseline, left.Size, opt))
                    { s.TableRow = true; break; }
                }
        }
    }
}
