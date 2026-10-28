namespace PdfEditor.TextEdit.FindReplace;

/// <summary>
/// 같은 각도·같은 기준선에서 이어진 글자들(쪼개진 글 연산을 한 줄로 잇는다). Text의 각 글자는 Map으로 Glyphs의 글자에 대응한다.
/// 글자 사이 틈이 0.25 em을 넘으면 가상 공백(Virtual)을 넣고 1.0 em을 넘으면 줄을 끊는다(0.4.0 확정 규칙).
/// </summary>
public sealed class LogicalLine
{
    public List<GlyphInfo> Glyphs = [];
    public string Text = "";
    /// <summary>Text의 글자 위치 → Glyphs 번호.</summary>
    public List<int> Map = [];
    public int Angle;
}

public static class LogicalLineBuilder
{
    /// <summary>기준선 허용 오차(글자 크기 배수).</summary>
    public const double BaselineTolerance = 0.15;
    /// <summary>이보다 넓은 틈은 가상 공백(글자 크기 배수).</summary>
    public const double SpaceGap = 0.25;
    /// <summary>이보다 넓은 틈은 줄 끊음(글자 크기 배수).</summary>
    public const double BreakGap = 1.0;

    /// <summary>이 비율 넘게 가로로 겹친 글자는 같은 자리에 덧찍은 것으로 본다(보통 이웃 글자는 거의 안 겹친다).</summary>
    private const double OverprintOverlap = 0.5;
    private const double Bucket = 16;

    /// <summary>
    /// 한 줄(같은 기준선) 글자들을 글 쓰기 순서로 훑으며, 이미 놓인 글자와 크게 겹치는 글자는 다음 층으로 보낸다.
    /// 겹침이 없으면 층이 하나라 결과가 이전과 같다.
    /// </summary>
    private static List<List<(GlyphInfo G, double V, double U0, double U1)>> SplitOverprinted(List<(GlyphInfo G, double V, double U0, double U1)> row)
    {
        var layers = new List<List<(GlyphInfo G, double V, double U0, double U1)>>();
        var grids = new List<Dictionary<long, List<(double A, double B)>>>();
        int lastLayer = 0;
        foreach (var t in row.OrderBy(t => t.G.StreamId).ThenBy(t => t.G.Op).ThenBy(t => t.G.Str).ThenBy(t => t.G.Code))
        {
            if (string.IsNullOrWhiteSpace(t.G.Text))
            {
                // 공백 글자는 잉크가 없어 겹침 판단에서 뺀다(새 글이 길어 뒤 공백을 덮어도 그 글을 쪼개지 않는다). 바로 앞에 놓인 글자의 층에 붙인다.
                if (layers.Count == 0) { layers.Add([]); grids.Add([]); lastLayer = 0; }
                layers[lastLayer].Add(t);
                continue;
            }
            double a = Math.Min(t.U0, t.U1), b = Math.Max(t.U0, t.U1);
            int k = 0;
            for (; k < layers.Count; k++) if (!Overlaps(grids[k], a, b)) break;
            if (k == layers.Count) { layers.Add([]); grids.Add([]); }
            layers[k].Add(t);
            lastLayer = k;
            long bk = (long)Math.Floor(a / Bucket);
            if (!grids[k].TryGetValue(bk, out var l)) grids[k][bk] = l = [];
            l.Add((a, b));
        }
        return layers;
    }

    private static bool Overlaps(Dictionary<long, List<(double A, double B)>> grid, double a, double b)
    {
        double w = b - a;
        if (w < 0.01) return false;
        long bk = (long)Math.Floor(a / Bucket);
        for (long k = bk - 1; k <= bk + 1; k++)
        {
            if (!grid.TryGetValue(k, out var l)) continue;
            foreach (var (c, d) in l)
            {
                double m = Math.Min(w, d - c);
                if (m < 0.01) continue;
                if (Math.Min(b, d) - Math.Max(a, c) > OverprintOverlap * m) return true;
            }
        }
        return false;
    }

    public static List<LogicalLine> Build(IReadOnlyList<GlyphInfo> glyphs)
    {
        var lines = new List<LogicalLine>();
        foreach (var byAngle in glyphs.Where(g => !g.Hidden && g.Size > 0.01).GroupBy(g => g.Angle))
        {
            double th = byAngle.Key * Math.PI / 180, dx = Math.Cos(th), dy = Math.Sin(th), nx = -dy, ny = dx;
            var list = byAngle.Select(g => (G: g, V: g.X0 * nx + g.Y0 * ny, U0: g.X0 * dx + g.Y0 * dy, U1: g.X1 * dx + g.Y1 * dy)).OrderBy(t => t.V).ToList();
            for (int s = 0; s < list.Count;)
            {
                int e = s + 1;
                double meanV = list[s].V;
                while (e < list.Count && Math.Abs(list[e].V - meanV) <= BaselineTolerance * list[s].G.Size)
                {
                    meanV = (meanV * (e - s) + list[e].V) / (e - s + 1);
                    e++;
                }
                // 겹쳐 찍은 글(굵게 보이려고 같은 글을 조금 비껴 두 번 찍은 도면)은 층으로 나눈다. 안 나누면 두 벌의 글자가 x 순서로 섞여("VVIISS") 낱말을 못 찾는다(실제 도면 결함).
                foreach (var layer in SplitOverprinted(list.GetRange(s, e - s)))
                {
                    var row = layer.OrderBy(t => t.U0).ToList();
                    LogicalLine? cur = null;
                    double prevU1 = 0;
                    foreach (var t in row)
                    {
                        double gap = t.U0 - prevU1;
                        if (cur is null || gap > BreakGap * t.G.Size) { cur = new LogicalLine { Angle = byAngle.Key }; lines.Add(cur); }
                        else if (gap > SpaceGap * t.G.Size)
                        {
                            cur.Glyphs.Add(new GlyphInfo { Text = " ", Virtual = true, Op = -1 });
                            cur.Text += " ";
                            cur.Map.Add(cur.Glyphs.Count - 1);
                        }
                        cur.Glyphs.Add(t.G);
                        foreach (char _ in t.G.Text) cur.Map.Add(cur.Glyphs.Count - 1);
                        cur.Text += t.G.Text;
                        prevU1 = Math.Max(t.U1, t.U0);
                    }
                }
                s = e;
            }
        }
        return lines;
    }
}
