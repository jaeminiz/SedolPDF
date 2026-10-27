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
                var row = list.GetRange(s, e - s).OrderBy(t => t.U0).ToList();
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
                s = e;
            }
        }
        return lines;
    }
}
