using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;

namespace PdfEditor.TextEdit.Paragraphs;

/// <summary>읽을 수 없는 이유(화면 문구는 앱이 리소스에서 고른다).</summary>
public enum ParagraphReadRefusal
{
    None,
    /// <summary>글 객체가 아니거나 이미 지워졌다.</summary>
    NotText,
    /// <summary>폼 XObject(양식·재사용 그림) 안의 글이다 — 폼 전체만 편집할 수 있다(V15).</summary>
    InForm,
    /// <summary>쪽 좌표 0·90·180·270°가 아닌 각도의 글이다.</summary>
    UnsupportedAngle,
    /// <summary>보이지 않는 글(렌더 모드 3·투명)이다.</summary>
    Hidden,
}

/// <summary>클릭한 줄 주변에서 읽은 글 객체와 장애물. <see cref="ObjectsRead"/>는 후보 글 객체 수(5,000줄 도면에서도 <see cref="ParagraphReader.MaxObjects"/> 이하).</summary>
public sealed record ParagraphInput(ImmutableArray<TextRun> Runs, ImmutableArray<Obstacle> Obstacles, ObjectRef Seed, int ObjectsRead, ParagraphReadRefusal Refusal)
{
    public static ParagraphInput Refused(ObjectRef seed, ParagraphReadRefusal why) => new([], [], seed, 0, why);
}

/// <summary>추정 결과: 문단 모델(없으면 null)과 그 재료. <see cref="Lines"/>는 줄 조립 결과 전체.</summary>
public sealed record ParagraphDetection(ParagraphModel? Model, ImmutableArray<TextLine> Lines, ParagraphInput Input);

/// <summary>
/// 클릭한 줄 주변의 후보 글 객체만 PDFium 전용 스레드에서 읽어 <see cref="TextRun"/>(순수 데이터)으로 만든다(M5 C2).
/// <b>문단 파일 가운데 Pdf.Native를 쓰는 유일한 파일</b>이다. 쪽 전체 지도를 만들지 않고 비용은 후보 수로만 정해진다.
/// </summary>
public static class ParagraphReader
{
    /// <summary>후보 글 객체 상한(시작 줄에서 가까운 순). 5,000줄 도면(M5 V21)에서 읽기 비용을 일정하게 한다.</summary>
    public const int MaxObjects = 240;

    /// <summary>세로 후보 창: 시작 줄에서 이 줄 수(글자 크기 × 1.5)만큼 위·아래.</summary>
    public const int MaxLines = 60;

    private const int MaxObstacles = 2000;
    private const double DuplicateTolEm = 0.12;

    /// <summary>textState: 글 객체 번호 → 내용 스트림의 (Tc, Tz). 클릭 경로는 PDFium 데이터만 쓰므로 보통 null(자간 0·가로 비율 100 %).</summary>
    public static ParagraphInput Read(DocumentSession s, PageId page, ObjectRef seed, PageObjectIndex index,
        Func<int, (float Tc, float Tz)?>? textState = null)
    {
        s.Thread.AssertOnThread();
        var r = s.GetReplay(page);
        if (seed.IsInserted || r.Resolve(seed) is not { IsActive: true } so) return ParagraphInput.Refused(seed, ParagraphReadRefusal.NotText);
        if (so.Type == PdfPageObjectType.Form) return ParagraphInput.Refused(seed, ParagraphReadRefusal.InForm);
        if (so.Type != PdfPageObjectType.Text) return ParagraphInput.Refused(seed, ParagraphReadRefusal.NotText);
        var m = so.Matrix;
        int angle = Angle(m);
        if (angle < 0) return ParagraphInput.Refused(seed, ParagraphReadRefusal.UnsupportedAngle);
        if (!IsVisible(so)) return ParagraphInput.Refused(seed, ParagraphReadRefusal.Hidden);
        var seedEntry = index.Entries.FirstOrDefault(e => e.Ref == seed);
        if (seedEntry is null) return ParagraphInput.Refused(seed, ParagraphReadRefusal.NotText);

        double size = SizeOf(so);
        if (size <= 0.01) return ParagraphInput.Refused(seed, ParagraphReadRefusal.NotText);
        var (_, _, sv0, sv1) = Geo.Project(seedEntry.Bounds, angle);
        double vc = (sv0 + sv1) / 2;

        // 후보: 시작 줄(같은 기준선대)의 가로 범위와 겹치는 글 객체를 시작 줄에서 가까운 순으로
        var texts = new List<(PageObjectEntry E, double U0, double U1, double Dv)>();
        foreach (var e in index.Entries)
        {
            if (e.Type != PdfPageObjectType.Text || e.Ref.IsInserted) continue;
            var (u0, u1, v0, v1) = Geo.Project(e.Bounds, angle);
            texts.Add((e, u0, u1, Math.Abs((v0 + v1) / 2 - vc)));
        }
        double rowMin = double.MaxValue, rowMax = double.MinValue;
        foreach (var t in texts)
            if (t.Dv <= 0.5 * size) { rowMin = Math.Min(rowMin, t.U0); rowMax = Math.Max(rowMax, t.U1); }
        if (rowMin > rowMax) { var (u0, u1, _, _) = Geo.Project(seedEntry.Bounds, angle); rowMin = u0; rowMax = u1; }
        double reach = 1.0 * size, maxDv = MaxLines * 1.5 * size;
        var cands = texts.Where(t => t.U1 >= rowMin - reach && t.U0 <= rowMax + reach && t.Dv <= maxDv).OrderBy(t => t.Dv).ToList();
        var picked = new List<PageObjectEntry>();
        double lastDv = 0;
        foreach (var t in cands)
        {
            if (picked.Count >= MaxObjects && t.Dv - lastDv > 0.35 * size) break;   // 마지막 줄은 끝까지 채운다
            picked.Add(t.E);
            lastDv = t.Dv;
        }

        // 글 읽기
        var text = s.GetLoadedText(page);
        var runs = new List<TextRun>();
        var empties = new List<(PageObjectEntry E, PdfPageObject O)>();
        PdfRectF? win = null;
        foreach (var e in picked)
        {
            win = win is null ? e.Bounds : Union(win.Value, e.Bounds);
            var o = r.Resolve(e.Ref);
            if (o is null || !o.IsActive || o.Type != PdfPageObjectType.Text) continue;
            string? str = o.GetText(text);
            if (string.IsNullOrWhiteSpace(str)) { empties.Add((e, o)); continue; }
            if (MakeRun(e.Ref, o, str, textState) is { } run) runs.Add(run);
        }
        // 겹쳐 찍은 같은 글: PDFium 글 쪽은 두 번째 객체의 글을 비워 준다 → 같은 자리의 대표 글 객체와 짝지어 같은 글로 넘기면 줄 조립이 한 줄로 합치고 원래 참조를 보존한다.
        foreach (var (e, o) in empties)
        {
            if (MakeRun(e.Ref, o, "·", textState) is not { } probe) continue;
            var rep = runs.FirstOrDefault(x => x.AngleDeg == probe.AngleDeg && Math.Abs(x.SizePt - probe.SizePt) <= 0.03 * probe.SizePt
                && Dist(x.Origin, probe.Origin) <= DuplicateTolEm * probe.SizePt);
            if (rep is not null) runs.Add(probe with { Text = rep.Text, Bounds = rep.Bounds });
        }

        // 장애물: 읽은 후보 창 안의 선·칠·그림(쪽 전체 테두리 제외)
        var obstacles = ImmutableArray.CreateBuilder<Obstacle>();
        if (win is { } w0)
        {
            var w = w0.Inflate((float)(2 * size));
            foreach (var e in index.Entries)
            {
                if (obstacles.Count >= MaxObstacles) break;
                if (e.IsFrame || e.Type is not (PdfPageObjectType.Path or PdfPageObjectType.Image)) continue;
                if (!Intersects(w, e.Bounds)) continue;
                if (e.Type == PdfPageObjectType.Image) { obstacles.Add(new Obstacle(e.Bounds, ObstacleKind.Image)); continue; }
                AddPathObstacles(e, e.Ref.IsInserted ? null : r.Resolve(e.Ref), obstacles);
            }
        }
        return new ParagraphInput([.. runs], obstacles.ToImmutable(), seed, picked.Count, ParagraphReadRefusal.None);
    }

    /// <summary>읽기 + 줄 조립 + 문단 묶기(순수 부분은 PDFium 없이). 시작 줄을 못 찾으면 Model은 null.</summary>
    public static ParagraphDetection Detect(DocumentSession s, PageId page, ObjectRef seed, PageObjectIndex index,
        ParagraphOptions? options = null, Func<int, (float Tc, float Tz)?>? textState = null)
        => Detect(Read(s, page, seed, index, textState), options);

    public static ParagraphDetection Detect(ParagraphInput input, ParagraphOptions? options = null)
    {
        if (input.Refusal != ParagraphReadRefusal.None || input.Runs.IsEmpty) return new ParagraphDetection(null, [], input);
        var lines = LineAssembler.Assemble(input.Runs, input.Obstacles, options);
        int seedLine = -1;
        for (int i = 0; i < lines.Length && seedLine < 0; i++)
            if (lines[i].AllRefs.Contains(input.Seed)) seedLine = i;
        var model = seedLine < 0 ? null : ParagraphDetector.Detect(lines, seedLine, input.Obstacles, options);
        return new ParagraphDetection(model, lines, input);
    }

    // ---------------------------------------------------------------- 읽기 조각
    private static TextRun? MakeRun(ObjectRef rf, PdfPageObject o, string text, Func<int, (float Tc, float Tz)?>? state)
    {
        var m = o.Matrix;
        int angle = Angle(m);
        if (angle < 0 || !IsVisible(o)) return null;
        double size = SizeOf(o);
        if (size <= 0.01) return null;
        var (ux, uy, vx, vy) = Geo.Axes(angle);
        var (u0, u1, _, _) = Geo.Project(o.Bounds, angle);
        double v = m.E * vx + m.F * vy;
        PdfPointF P(double lu, double lv) => new((float)(lu * ux + lv * vx), (float)(lu * uy + lv * vy));
        var b = PdfRectF.FromPoints([P(u0, v - 0.2 * size), P(u1, v - 0.2 * size), P(u0, v + 0.8 * size), P(u1, v + 0.8 * size)]);
        var f = o.Font;
        var fc = o.FillColor;
        var color = fc is { } c ? new PdfColor(c.R, c.G, c.B) : new PdfColor(0, 0, 0);
        var st = state?.Invoke(rf.OriginalIndex);
        return new TextRun(rf, text, new PdfPointF(m.E, m.F), angle, (float)size, $"{f?.BaseName}|{f?.Embedded}", color, st?.Tc ?? 0, st?.Tz ?? 100, b, true);
    }

    private static int Angle(PdfMatrix m)
    {
        double ang = Math.Atan2(m.B, m.A) * 180 / Math.PI;
        int a = (int)Math.Round(((ang % 360) + 360) % 360 / 90.0) % 4 * 90;
        double diff = Math.Abs(((ang - a + 540) % 360) - 180);
        return diff > 3 ? -1 : a;
    }

    private static bool IsVisible(PdfPageObject o)
    {
        int tr = o.TextRenderMode;
        return tr != 3 && !(tr == 0 && o.FillColor is { A: 0 });
    }

    private static double SizeOf(PdfPageObject o)
    {
        var m = o.Matrix;
        return o.FontSize * Math.Sqrt((double)m.C * m.C + (double)m.D * m.D);
    }

    private static double Dist(PdfPointF a, PdfPointF b) => Math.Sqrt(((double)a.X - b.X) * ((double)a.X - b.X) + ((double)a.Y - b.Y) * ((double)a.Y - b.Y));

    private static PdfRectF Union(PdfRectF a, PdfRectF b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Bottom, b.Bottom), Math.Max(a.Right, b.Right), Math.Max(a.Top, b.Top));

    private static bool Intersects(PdfRectF a, PdfRectF b) => a.Left <= b.Right && b.Left <= a.Right && a.Bottom <= b.Top && b.Bottom <= a.Top;

    /// <summary>선(획 있는 경로)은 선분마다, 칠한 가는 막대는 경계 상자 하나를 장애물로(M5 V17 탐침과 같은 규칙).</summary>
    private static void AddPathObstacles(PageObjectEntry e, PdfPageObject? o, ImmutableArray<Obstacle>.Builder into)
    {
        var b = e.Bounds;
        double w = b.Width, h = b.Height;
        var g = e.Geometry;
        bool stroked = g is { Stroked: true } || (g is null && w > 2.5 && h > 2.5 && o is { StrokeWidth: > 0 });
        if (stroked)
        {
            if (g is null) { into.Add(new Obstacle(b, ObstacleKind.Line)); return; }
            foreach (var sp in g.Subpaths)
            {
                var pts = sp.Points;
                int n = pts.Length;
                for (int i = 0; i + 1 < n + (sp.Closed ? 1 : 0); i++)
                {
                    var p0 = pts[i];
                    var p1 = pts[(i + 1) % n];
                    into.Add(new Obstacle(new PdfRectF(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y)), ObstacleKind.Line));
                }
            }
            return;
        }
        if (Math.Min(w, h) <= 2.5) into.Add(new Obstacle(b, ObstacleKind.Fill));
    }
}
