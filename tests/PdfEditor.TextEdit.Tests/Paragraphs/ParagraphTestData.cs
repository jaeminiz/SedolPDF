using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.TestFixtures;
using PdfEditor.TextEdit.Paragraphs;

namespace PdfEditor.TextEdit.Tests.Paragraphs;

/// <summary>문단 추정 시험용 입력 만들기: 손으로 짠 글 객체 + 합성 샘플 PDF(PDFium으로 읽어 TextRun으로 바꿈).</summary>
internal static class ParagraphTestData
{
    private static int _next;

    /// <summary>
    /// 로컬 좌표(u 글 방향, v 위)로 글 객체를 만든다. 글 폭은 글자당 0.5 em. 기준선 원점 = (u, v).
    /// </summary>
    public static TextRun Run(string text, double u, double v, double size = 10, string font = "F1", int angle = 0, double? width = null,
        bool visible = true, PdfColor? color = null, float tz = 100)
    {
        double w = width ?? text.Length * 0.5 * size;
        var (ux, uy, vx, vy) = Geo.Axes(angle);
        PdfPointF P(double lu, double lv) => new((float)(lu * ux + lv * vx), (float)(lu * uy + lv * vy));
        var pts = new[] { P(u, v - 0.2 * size), P(u + w, v - 0.2 * size), P(u, v + 0.8 * size), P(u + w, v + 0.8 * size) };
        return new TextRun(ObjectRef.Original(_next++), text, P(u, v), angle, (float)size, font, color ?? new PdfColor(0, 0, 0), 0, tz,
            PdfRectF.FromPoints(pts), visible);
    }

    /// <summary>왼쪽 맞춤 문단: 줄마다 글 객체 하나. widths가 있으면 줄 폭(pt)을 지정한다.</summary>
    public static List<TextRun> Para(string[] texts, double u, double vTop, double size = 10, double pitch = 12, string font = "F1",
        int angle = 0, double[]? widths = null, double firstIndent = 0)
    {
        var l = new List<TextRun>();
        for (int i = 0; i < texts.Length; i++)
            l.Add(Run(texts[i], u + (i == 0 ? firstIndent : 0), vTop - i * pitch, size, font, angle, widths?[i]));
        return l;
    }

    private static readonly string[] Lead = ["when", "alpha", "then", "ocean", "under", "bridge", "every", "moving", "quiet", "silver"];

    /// <summary>열 폭을 거의 채운 영문 문장 줄들(양쪽 맞춤 문단 흉내): 앞줄 폭 = width, 끝 줄은 짧게.</summary>
    public static List<TextRun> Body(int lines, double u, double vTop, double width = 300, double size = 10, double pitch = 12, string font = "F1",
        int angle = 0, double lastWidth = 120, double firstIndent = 0, string tag = "")
    {
        var l = new List<TextRun>();
        for (int i = 0; i < lines; i++)
        {
            bool last = i == lines - 1;
            double w = last ? lastWidth : width - (i == 0 ? firstIndent : 0);
            string text = $"{Lead[i % Lead.Length]} quick brown fox{tag} jumps over lazy dog number {i} while reading the long sentence";
            text = text[..Math.Min(text.Length, (int)(w / (0.5 * size)))];
            l.Add(Run(text, u + (i == 0 ? firstIndent : 0), vTop - i * pitch, size, font, angle, w));
        }
        return l;
    }

    // ---------------------------------------------------------------- 합성 샘플 PDF 읽기
    public sealed record SamplePage(ParagraphSample Sample, IReadOnlyList<TextRun> Runs, IReadOnlyList<Obstacle> Obstacles,
        System.Collections.Immutable.ImmutableArray<TextLine> Lines, Dictionary<TextLine, TruthLine> Match, PageData Data);

    public static TextRun ToTextRun(Run r)
    {
        var (ux, uy, vx, vy) = Geo.Axes(r.Angle);
        PdfPointF P(double lu, double lv) => new((float)(lu * ux + lv * vx), (float)(lu * uy + lv * vy));
        var b = PdfRectF.FromPoints([P(r.U0, r.V - 0.2 * r.Size), P(r.U1, r.V - 0.2 * r.Size), P(r.U0, r.V + 0.8 * r.Size), P(r.U1, r.V + 0.8 * r.Size)]);
        return new TextRun(ObjectRef.Original(r.Obj), r.Text, new PdfPointF((float)r.OX, (float)r.OY), r.Angle, (float)r.Size, r.Font,
            new PdfColor((byte)(r.Color >> 16), (byte)(r.Color >> 8), (byte)r.Color), 0, 100, b, true);
    }

    public static Obstacle ToObstacle(Obst o) =>
        new(new PdfRectF((float)o.L, (float)o.B, (float)o.R, (float)o.T), o.IsImage ? ObstacleKind.Image : ObstacleKind.Line);

    public static SamplePage Read(PdfiumThread t, ParagraphSample s)
    {
        var pd = ParagraphProbe.ReadPdfium(t, s.Path, 0);
        var runs = pd.Runs.Select(ToTextRun).ToList();
        var obs = pd.Obstacles.Select(ToObstacle).ToList();
        var lines = LineAssembler.Assemble(runs, obs);
        var match = new Dictionary<TextLine, TruthLine>();
        var used = new HashSet<TruthLine>();
        foreach (var l in lines)
        {
            var tl = s.Truth.FirstOrDefault(x => !used.Contains(x) && V17ToV21Probes.Norm(x.Text) == V17ToV21Probes.Norm(l.Text));
            if (tl is null) continue;
            used.Add(tl);
            match[l] = tl;
        }
        return new SamplePage(s, runs, obs, lines, match, pd);
    }

    private static IReadOnlyList<ParagraphSample>? _samples;

    public static IReadOnlyList<ParagraphSample> Samples() =>
        _samples ??= ParagraphPdfFactory.CreateAll(Path.Combine(Path.GetTempPath(), "m5task2-samples"));

    public static ParagraphSample Sample(string name) => Samples().First(s => s.Name == name);

    /// <summary>문단 묶음을 정답과 비교: 모두 같은 정답 문단·줄 수까지 같으면 정확.</summary>
    public enum Verdict { Exact, Partial, Wrong, Unknown }

    public static Verdict Judge(SamplePage p, ParagraphModel m)
    {
        var tl = m.Lines.Select(l => p.Match.GetValueOrDefault(l)).ToList();
        if (tl.Any(x => x is null)) return Verdict.Unknown;
        var pids = tl.Select(x => x!.ParagraphId).Distinct().ToList();
        if (pids.Count != 1) return Verdict.Wrong;
        return p.Sample.Truth.Count(x => x.ParagraphId == pids[0]) == m.Lines.Length ? Verdict.Exact : Verdict.Partial;
    }
}
