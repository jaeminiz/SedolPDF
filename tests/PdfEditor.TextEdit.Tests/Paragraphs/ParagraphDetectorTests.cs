using System.Collections.Immutable;
using System.Text;
using Pdf.Native;
using PdfEditor.TextEdit.Paragraphs;
using static PdfEditor.TextEdit.Tests.Paragraphs.ParagraphTestData;

namespace PdfEditor.TextEdit.Tests.Paragraphs;

public sealed class ParagraphDetectorTests : IDisposable
{
    private readonly PdfiumThread _t = new();
    public void Dispose() => _t.Dispose();

    private static readonly ParagraphOptions Opt = new();

    private static ImmutableArray<TextLine> Lines(IEnumerable<TextRun> runs, IReadOnlyList<Obstacle>? obs = null) =>
        LineAssembler.Assemble([.. runs], obs);

    private static ParagraphModel DetectTop(ImmutableArray<TextLine> lines, IReadOnlyList<Obstacle>? obs = null, int seed = 0) =>
        ParagraphDetector.Detect(lines, seed, obs ?? [], Opt)!;

    // ------------------------------------------------------------------ 정렬·들여쓰기
    [Fact]
    public void Paragraph_JustifiedBody_ThreeLines()
    {
        var lines = Lines(Body(3, 60, 700));
        var m = DetectTop(lines);
        Assert.True(m.IsParagraph);
        Assert.Equal(3, m.Lines.Length);
        Assert.Equal(Alignment.Justified, m.Align);
        Assert.True(m.Confidence >= Opt.Threshold);
        Assert.Equal(12, m.Leading, 2);
        Assert.Equal(0, m.FirstLineIndent);
        Assert.Null(m.LowConfidenceReason);
    }

    [Fact]
    public void Paragraph_JustifiedBody_FromSample_IsExact()
    {
        var p = Read(_t, Sample("doc-en-justified"));
        var parts = ParagraphDetector.Partition(p.Lines, p.Obstacles, Opt);
        var exact = parts.Where(m => m.IsParagraph && Judge(p, m) == Verdict.Exact).ToList();
        Assert.True(exact.Count >= 3);
        Assert.Contains(exact, m => m.Align == Alignment.Justified);
        Assert.DoesNotContain(parts, m => m.IsParagraph && Judge(p, m) == Verdict.Wrong);
    }

    [Fact]
    public void Paragraph_FirstLineIndent_Detected()
    {
        var lines = Lines(Body(4, 60, 700, firstIndent: 12));
        var m = DetectTop(lines);
        Assert.Equal(4, m.Lines.Length);
        Assert.Equal(12, m.FirstLineIndent, 1);
        Assert.Contains(m.Align, new[] { Alignment.Left, Alignment.Justified });
        // 들여쓴 줄은 새 문단의 첫 줄: 앞 문단 끝 줄(짧음) 다음에 들여쓴 줄이 오면 둘로 나뉜다
        var two = Body(3, 60, 700, lastWidth: 200).Concat(Body(3, 60, 700 - 3 * 12, firstIndent: 12, tag: "x"));
        var parts = ParagraphDetector.Partition(Lines(two), [], Opt).Where(x => x.IsParagraph).ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, x => Assert.Equal(3, x.Lines.Length));
    }

    [Fact]
    public void Centered_AndRightAligned_Detected()
    {
        string[] t = ["a long centered title line one two three", "centered title second line of text", "short third centered line"];
        var center = Lines(new[] { 200.0, 260.0, 180.0 }.Select((w, i) => Run(t[i], 300 - w / 2, 700 - i * 14, 10, width: w)));
        var c = DetectTop(center);
        Assert.Equal(3, c.Lines.Length);
        Assert.Equal(Alignment.Center, c.Align);
        var right = Lines(new[] { 200.0, 260.0, 180.0 }.Select((w, i) => Run(t[i], 500 - w, 700 - i * 14, 10, width: w)));
        var r = DetectTop(right);
        Assert.Equal(3, r.Lines.Length);
        Assert.Equal(Alignment.Right, r.Align);
    }

    [Fact]
    public void CenteredAndRight_FromSample_AreFound()
    {
        var p = Read(_t, Sample("doc-center-right"));
        var parts = ParagraphDetector.Partition(p.Lines, p.Obstacles, Opt).Where(m => m.IsParagraph).ToList();
        Assert.DoesNotContain(parts, m => Judge(p, m) == Verdict.Wrong);
        // 오른쪽 맞춤 2줄 문단은 신뢰도가 문턱(0.62) 아래(0.55)라 보수적으로 한 줄 편집으로 남는다(알려진 미탐). 문턱을 풀면 오른쪽 맞춤으로 분류된다.
        var loose = ParagraphDetector.Partition(p.Lines, p.Obstacles, Opt with { Threshold = 0.5 }).Where(m => m.IsParagraph).ToList();
        Assert.Contains(loose, m => m.Align == Alignment.Right && Judge(p, m) == Verdict.Exact);
    }

    // ------------------------------------------------------------------ 표·칸
    [Fact]
    public void Table_CellsNeverMerged()
    {
        // 3열 × 3행 짧은 칸(같은 기준선에 칸 여러 개) → 모든 줄이 표 행: 한 줄 편집으로
        var runs = new List<TextRun>();
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++) runs.Add(Run($"cell {r}{c}", 40 + c * 100, 700 - r * 12));
        var lines = Lines(runs);
        Assert.All(lines, l => Assert.True(l.TableRow));
        for (int i = 0; i < lines.Length; i++)
        {
            var m = DetectTop(lines, seed: i);
            Assert.Single(m.Lines);
            Assert.Equal(ParagraphReasons.TableRow, m.LowConfidenceReason);
        }
        Assert.DoesNotContain(ParagraphDetector.Partition(lines, [], Opt), m => m.Lines.Length > 1);
    }

    [Fact]
    public void Table_WithRuledGrid_NeverMerged()
    {
        // 긴 글이 들어 있는 칸이라도 칸막이 세로선이 있으면 옆 칸과 같은 줄로 합쳐지지 않는다
        var runs = new List<TextRun>
        {
            Run("left cell has quite a long description text", 10, 700, width: 200),
            Run("right cell also has a long description", 215, 700, width: 200),
            Run("left cell second row long description text", 10, 688, width: 200),
            Run("right cell second row long description text", 215, 688, width: 200),
        };
        var vrule = new Obstacle(new PdfRectF(211, 680, 211.6f, 712), ObstacleKind.Line);
        var lines = Lines(runs, [vrule]);
        Assert.Equal(4, lines.Length);
        Assert.All(lines, l => Assert.True(l.TableRow));
        Assert.DoesNotContain(ParagraphDetector.Partition(lines, [vrule], Opt), m => m.Lines.Length > 1);
    }

    [Fact]
    public void Table_Samples_NoTableMerge()
    {
        foreach (var name in new[] { "dwg-titleblock-rules", "dwg-table-norules", "doc-toc", "dwg-legend", "dwg-dimensions" })
        {
            var p = Read(_t, Sample(name));
            foreach (var m in ParagraphDetector.Partition(p.Lines, p.Obstacles, Opt).Where(m => m.Lines.Length > 1))
                Assert.True(Judge(p, m) != Verdict.Wrong, $"{name}: 잘못 묶음 {string.Join(" | ", m.Lines.Select(l => l.Text))}");
        }
    }

    [Fact]
    public void TitleBlock_LabelAndValueSeparate()
    {
        var p = Read(_t, Sample("dwg-titleblock-rules"));
        var parts = ParagraphDetector.Partition(p.Lines, p.Obstacles, Opt);
        // 라벨(7pt)과 값(10pt) 같은 칸 위아래: 다른 글자 크기라 묶이지 않는다
        foreach (var m in parts.Where(m => m.Lines.Length > 1))
        {
            var truth = m.Lines.Select(l => p.Match[l]).ToList();
            Assert.False(truth.Any(x => x.InTable) && truth.Select(x => x.ParagraphId).Distinct().Count() > 1);
        }
        var label = p.Lines.First(l => l.Text == "TITLE");
        var value = p.Lines.First(l => l.Text == "PIPING ARRANGEMENT");
        Assert.NotEqual(label.Size, value.Size);
        var m1 = ParagraphDetector.Detect(p.Lines, p.Lines.IndexOf(label), p.Obstacles, Opt)!;
        Assert.DoesNotContain(value, m1.Lines);
        Assert.Single(m1.Lines);
    }

    [Fact]
    public void TwoColumns_NotMergedAcross()
    {
        var p = Read(_t, Sample("doc-2col"));
        var parts = ParagraphDetector.Partition(p.Lines, p.Obstacles, Opt).Where(m => m.Lines.Length > 1).ToList();
        Assert.True(parts.Count >= 3);
        Assert.All(parts, m => Assert.NotEqual(Verdict.Wrong, Judge(p, m)));
        Assert.All(parts, m => Assert.True(m.Lines.All(l => Math.Abs(l.U0 - m.Lines[0].U0) < 5)));   // 한 단 안의 줄만
        // 같은 입력을 손으로: 두 단 문단이 같은 기준선에 나란히
        var runs = Body(4, 50, 700, width: 240, tag: "L").Concat(Body(4, 310, 700, width: 240, tag: "R"));
        var two = Lines(runs).ToList();
        var ps = ParagraphDetector.Partition(two, [], Opt).Where(m => m.IsParagraph).ToList();
        Assert.Equal(2, ps.Count);
        Assert.All(ps, m => Assert.Equal(4, m.Lines.Length));
    }

    // ------------------------------------------------------------------ 섞임·겹침·간격
    [Fact]
    public void MixedFontLine_NotGrouped()
    {
        var p = Read(_t, Sample("doc-mixed-bold"));
        Assert.Contains(p.Lines, l => l.Mixed);
        for (int i = 0; i < p.Lines.Length; i++)
        {
            var m = ParagraphDetector.Detect(p.Lines, i, p.Obstacles, Opt)!;
            Assert.Single(m.Lines);
            Assert.Equal(ParagraphReasons.MixedStyle, m.LowConfidenceReason);
        }
    }

    [Fact]
    public void DuplicateOverlaidText_TreatedAsOne()
    {
        var p = Read(_t, Sample("doc-overlay-bold"));
        var parts = ParagraphDetector.Partition(p.Lines, p.Obstacles, Opt).Where(m => m.IsParagraph).ToList();
        Assert.True(parts.Count >= 3);
        Assert.All(parts, m => Assert.Equal(Verdict.Exact, Judge(p, m)));
        // 겹쳐 찍은 글의 원래 객체도 문단의 편집 대상에 모두 들어 있다(PDFium이 겹친 글을 한 벌로 돌려주는 점은 LineAssemblerTests 참고)
        var body = Body(4, 60, 700);
        var runs = body.Concat(body.Select(r => r with { Ref = PdfEditor.Document.Content.ObjectRef.Original(r.Ref.OriginalIndex + 1000), Origin = new PdfPointF(r.Origin.X + 0.4f, r.Origin.Y), Bounds = r.Bounds.Offset(0.4f, 0) }));
        var m = DetectTop(Lines(runs));
        Assert.Equal(4, m.Lines.Length);
        Assert.Equal(8, m.AllRefs.Count());
    }

    [Fact]
    public void GapInconsistent_StopsGrouping()
    {
        var runs = Body(4, 60, 700).Concat([Run("the fifth line is spaced further away from the rest of paragraph", 60, 700 - 36 - 20, 10, width: 300)]);
        var lines = Lines(runs);
        Assert.Equal(5, lines.Length);
        var m = DetectTop(lines);
        Assert.Equal(4, m.Lines.Length);
        Assert.DoesNotContain(lines[4], m.Lines);
    }

    [Fact]
    public void LowConfidence_FallsBackToSingleLine()
    {
        var lines = Lines(new[] { Run("alpha beta gamma", 60, 700, width: 80), Run("delta epsilon zeta", 60, 688, width: 80) });
        var m = DetectTop(lines);
        Assert.Single(m.Lines);
        Assert.Equal(ParagraphReasons.LowConfidence, m.LowConfidenceReason);
        Assert.True(m.Confidence < Opt.Threshold);
        Assert.False(m.IsParagraph);
        // 사용자가 문턱을 낮추면 같은 입력이 묶인다 — 문턱은 옵션 한 곳
        var lenient = ParagraphDetector.Detect(lines, 0, [], Opt with { Threshold = 0.2 })!;
        Assert.Equal(2, lenient.Lines.Length);
    }

    [Fact]
    public void ShortLine_EndsParagraph_AndLoneLineHasNoReason()
    {
        // 짧은 줄(열 폭의 절반 이하)은 문단 끝: 다음 문단이 바로 이어져도 합쳐지지 않는다
        var runs = Body(3, 60, 700, lastWidth: 100).Concat(Body(3, 60, 700 - 36, tag: "b"));
        var parts = ParagraphDetector.Partition(Lines(runs), [], Opt).Where(m => m.IsParagraph).ToList();
        Assert.Equal(2, parts.Count);
        var lone = DetectTop(Lines([Run("a single line of text on its own on the page here", 60, 400, width: 250)]));
        Assert.Single(lone.Lines);
        Assert.Null(lone.LowConfidenceReason);
        Assert.Equal(12, lone.Leading, 2);                 // C18: 한 줄 문단은 글자 크기 × 1.2
        Assert.Null(ParagraphDetector.Detect(lone.Lines, 5, [], Opt));
    }

    [Fact]
    public void BulletList_EachItemIsOwnParagraph()
    {
        var p = Read(_t, Sample("doc-heading-bullets"));
        var parts = ParagraphDetector.Partition(p.Lines, p.Obstacles, Opt);
        var items = parts.Where(m => m.Lines[0].Text.StartsWith('•')).ToList();
        Assert.Equal(3, items.Count);
        Assert.All(items, m => { Assert.True(m.IsParagraph); Assert.Equal(Verdict.Exact, Judge(p, m)); });
        Assert.All(parts, m => Assert.InRange(m.Lines.Count(l => l.Text.StartsWith('•')), 0, 1));
        // 글머리표로 시작한 줄 뒤에 이어 붙은 줄도 새 항목으로 시작하는 줄과는 섞이지 않는다
        var runs = new List<TextRun>();
        for (int i = 0; i < 3; i++)
        {
            runs.Add(Run("•", 50, 700 - i * 24, width: 4));
            runs.Add(Run($"item number {i} that is long enough", 66, 700 - i * 24, width: 250));
            runs.Add(Run("continuation of the same item text here", 66, 688 - i * 24, width: 250));
        }
        var items2 = ParagraphDetector.Partition(Lines(runs), [], Opt).Where(m => m.IsParagraph).ToList();
        Assert.Equal(3, items2.Count);
        Assert.All(items2, m => Assert.Equal(2, m.Lines.Length));
    }

    // ------------------------------------------------------------------ 각도
    [Fact]
    public void Rotated90_Paragraph_Detected()
    {
        var lines = Lines(Body(4, 100, 300, angle: 90));
        var m = DetectTop(lines);
        Assert.Equal(4, m.Lines.Length);
        Assert.All(m.Lines, l => Assert.Equal(90, l.Angle));
        Assert.Equal(Alignment.Justified, m.Align);
        var p = Read(_t, Sample("dwg-rot90"));
        var parts = ParagraphDetector.Partition(p.Lines, p.Obstacles, Opt).Where(x => x.IsParagraph).ToList();
        Assert.Contains(parts, x => x.Lines[0].Angle == 90);
        Assert.All(parts, x => Assert.NotEqual(Verdict.Wrong, Judge(p, x)));
    }

    [Fact]
    public void Rotated45_NotDetected()
    {
        var lines = Lines(Body(4, 100, 300, angle: 45));
        Assert.Empty(lines);
        Assert.Null(ParagraphDetector.Detect(lines, 0, [], Opt));
    }

    [Fact]
    public void DifferentAngles_NeverMerged()
    {
        var runs = Body(3, 60, 700).Concat(Body(3, 60, 600, angle: 180));
        var parts = ParagraphDetector.Partition(Lines(runs), [], Opt).Where(m => m.IsParagraph).ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, m => Assert.Single(m.Lines.Select(l => l.Angle).Distinct()));
    }

    // ------------------------------------------------------------------ 장애물
    [Fact]
    public void GrowthLimit_StopsAtObstacle()
    {
        var lines = Lines(Body(4, 60, 700));                    // 마지막 줄 기준선 664
        var m0 = DetectTop(lines);
        Assert.Equal(float.NegativeInfinity, m0.MaxBottom);
        var below = new Obstacle(new PdfRectF(0, 600, 400, 640), ObstacleKind.Fill);
        var m1 = DetectTop(lines, [below]);
        Assert.Equal(640, m1.MaxBottom, 1);
        Assert.Equal(4, m1.Lines.Length);                        // 문단 밖 장애물은 문단 자체를 바꾸지 않는다
        var aside = new Obstacle(new PdfRectF(450, 600, 500, 640), ObstacleKind.Fill);
        Assert.Equal(float.NegativeInfinity, DetectTop(lines, [aside]).MaxBottom);
        // 아래 다른 문단의 줄도 한계가 된다
        var withNext = Lines(Body(4, 60, 700).Concat(Body(2, 60, 600, tag: "n")));
        Assert.Equal(608, DetectTop(withNext).MaxBottom, 1);   // 아래 줄의 위쪽 끝 = 기준선 600 + 0.8 em
    }

    [Fact]
    public void FullPageFrame_IgnoredAsObstacle()
    {
        var lines = Lines(Body(4, 60, 700));
        var frame = new Obstacle(new PdfRectF(0, 0, 595, 842), ObstacleKind.Frame);
        var m = DetectTop(lines, [frame]);
        Assert.Equal(4, m.Lines.Length);
        Assert.Equal(float.NegativeInfinity, m.MaxBottom);
        // 같은 크기라도 테두리로 표시되지 않은 칠은 줄 사이를 막는다(그래서 Frame 구분이 필요하다)
        var fill = new Obstacle(new PdfRectF(0, 0, 595, 842), ObstacleKind.Fill);
        Assert.Single(DetectTop(lines, [fill]).Lines);
        // 줄 사이를 가로지르는 선은 문단을 끊는다
        var rule = new Obstacle(new PdfRectF(40, 696.6f, 400, 697.2f), ObstacleKind.Line);
        Assert.Single(DetectTop(lines, [rule]).Lines);
        // 문단 전체를 덮은 배경 그림은 막지 않는다
        var bg = new Obstacle(new PdfRectF(0, 600, 595, 750), ObstacleKind.Image);
        Assert.Equal(4, DetectTop(lines, [bg]).Lines.Length);
    }

    // ------------------------------------------------------------------ 사용자 조정
    [Fact]
    public void Extend_AddsAndRemovesContiguousLines()
    {
        var all = Lines(Body(3, 60, 700, lastWidth: 100).Concat(Body(3, 60, 640, tag: "b", lastWidth: 100)));
        var a = DetectTop(all);
        Assert.Equal(3, a.Lines.Length);
        // 아래로 1줄 더하기
        var plus = ParagraphDetector.Extend(a, 0, 1, all, [], Opt);
        Assert.Equal(4, plus.Lines.Length);
        Assert.Equal(all[3], plus.Lines[3]);
        Assert.Null(plus.LowConfidenceReason);
        // 아래에서 1줄 빼기·위에서 1줄 빼기
        Assert.Equal(2, ParagraphDetector.Extend(a, 0, -1, all, [], Opt).Lines.Length);
        var top = ParagraphDetector.Extend(a, -1, 0, all, [], Opt);
        Assert.Equal(all[1], top.Lines[0]);
        // 위로 더하기: 둘째 문단에서 시작해 첫 문단의 마지막 줄을 포함
        var b = DetectTop(all, seed: 3);
        var up = ParagraphDetector.Extend(b, 1, 0, all, [], Opt);
        Assert.Equal(all[2], up.Lines[0]);
        Assert.Equal(4, up.Lines.Length);
        // 이웃이 없으면 거기서 멈추고, 줄은 항상 1개 이상 남는다
        Assert.Equal(6, ParagraphDetector.Extend(a, 0, 10, all, [], Opt).Lines.Length);
        Assert.Single(ParagraphDetector.Extend(a, -5, -5, all, [], Opt).Lines);
        // 한 줄이 된 모델은 신뢰도 0, 사각형은 남은 줄 기준
        var one = ParagraphDetector.Extend(a, 0, -2, all, [], Opt);
        Assert.Equal(0, one.Confidence);
        Assert.Equal(all[0].Bounds, one.Box);
    }

    [Fact]
    public void Extend_AfterLowConfidenceFallback_LetsUserBuildParagraph()
    {
        var lines = Lines(new[] { Run("alpha beta gamma", 60, 700, width: 80), Run("delta epsilon zeta", 60, 688, width: 80) });
        var m = DetectTop(lines);
        Assert.Equal(ParagraphReasons.LowConfidence, m.LowConfidenceReason);
        var ext = ParagraphDetector.Extend(m, 0, 1, lines, [], Opt);
        Assert.Equal(2, ext.Lines.Length);
        Assert.True(ext.IsParagraph);                           // 사용자가 직접 만든 문단은 이유가 없다
    }

    // ------------------------------------------------------------------ 결정성·모음
    [Fact]
    public void Detect_Deterministic_SameInputSameOutput()
    {
        var runs = Body(4, 60, 700).Concat(Body(3, 60, 600, tag: "b")).Concat(Body(5, 330, 700, width: 200, tag: "c")).ToList();
        string Sig(IEnumerable<TextRun> rs)
        {
            var lines = LineAssembler.Assemble([.. rs]);
            return string.Join("\n", ParagraphDetector.Partition(lines, [], Opt).Select(m =>
                $"{m.Lines.Length}|{m.Align}|{m.Confidence:0.0000}|{m.Box}|{m.Leading}|{string.Join(",", m.Lines.Select(l => l.Text))}"));
        }
        string a = Sig(runs);
        Assert.Equal(a, Sig(runs));
        Assert.Equal(a, Sig(Enumerable.Reverse(runs)));          // 입력 순서와 무관
    }

    [Fact]
    public void Partition_ClaimsEachLineOnce_AndCoversAllLines()
    {
        var p = Read(_t, Sample("doc-ko-justified"));
        var parts = ParagraphDetector.Partition(p.Lines, p.Obstacles, Opt);
        var seen = parts.SelectMany(m => m.Lines).ToList();
        Assert.Equal(p.Lines.Length, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    // ------------------------------------------------------------------ 합성 샘플 정답표: 정밀도·표 병합 0 (Task 1 합격선을 정식 구현에 고정)
    [Fact]
    public void SyntheticSamples_PrecisionAndTableMerge_AtThreshold()
    {
        int docGroups = 0, docExact = 0, docTruthMulti = 0, docTruthExact = 0, wrong = 0, tableMerge = 0, unknown = 0, drawingGroups = 0, drawingExact = 0;
        var sb = new StringBuilder();
        foreach (var s in Samples())
        {
            var p = Read(_t, s);
            if (s.Kind == "Document") unknown += p.Lines.Count(l => !p.Match.ContainsKey(l));
            var parts = ParagraphDetector.Partition(p.Lines, p.Obstacles, Opt).Where(m => m.IsParagraph).ToList();
            var truthMulti = s.Truth.GroupBy(x => x.ParagraphId).Count(g => g.Count() >= 2);
            var exactPids = new HashSet<int>();
            foreach (var m in parts)
            {
                var v = Judge(p, m);
                if (s.Kind == "Document") docGroups++; else drawingGroups++;
                if (v == Verdict.Exact)
                {
                    exactPids.Add(p.Match[m.Lines[0]].ParagraphId);
                    if (s.Kind == "Document") docExact++; else drawingExact++;
                }
                else
                {
                    sb.AppendLine($"{s.Name}: {v} conf={m.Confidence:0.00} {string.Join(" | ", m.Lines.Select(l => l.Text[..Math.Min(30, l.Text.Length)]))}");
                    if (v == Verdict.Wrong) { wrong++; if (m.Lines.Any(l => p.Match[l].InTable)) tableMerge++; }
                }
            }
            if (s.Kind == "Document") { docTruthMulti += truthMulti; docTruthExact += exactPids.Count; }
        }
        Assert.True(wrong == 0 && tableMerge == 0, sb.ToString());
        Assert.Equal(0, unknown);
        Assert.True(docGroups > 0 && docExact >= 0.95 * docGroups, $"문서형 정밀도 {docExact}/{docGroups}\n{sb}");
        Assert.True(docTruthExact >= 0.9 * docTruthMulti, $"문서형 재현율 {docTruthExact}/{docTruthMulti}\n{sb}");
        Assert.Equal(drawingGroups, drawingExact);               // 도면형은 잘못 묶음 0(놓치는 것은 허용)
    }

    [Fact]
    public void Options_Defaults_AreFixedByTask1()
    {
        var o = new ParagraphOptions();
        Assert.Equal(0.62, o.Threshold);
        Assert.Equal(0.25, o.SpaceGapEm);
        Assert.Equal(1.0, o.CellGapEm);
        Assert.Equal(3.0, o.BulletGapEm);
        Assert.Equal(12, o.TableColumnEm);
        Assert.Equal(0.5, o.ShortLineFrac);
        Assert.Equal(1.0, o.MinPitchEm);
        Assert.Equal(2.2, o.MaxPitchEm);
        Assert.Equal(0.25, o.PitchTol);
        Assert.Equal(0.15, o.BaselineTolEm);
        Assert.Equal(0.03, o.SizeTol);
        Assert.Equal((0.55, 0.72, 0.80, 0.85), (o.ScoreLines2, o.ScoreLines3, o.ScoreLines4, o.ScoreLines5Plus));
        Assert.Equal((0.02, 0.06, 0.07, 0.03, 0.10), (o.PitchCvTight, o.PitchCvLoose, o.BonusPitchTight, o.BonusPitchLoose, o.PenaltyPitchUneven));
        Assert.Equal((1.1, 1.9, 0.10), (o.PitchOkMinEm, o.PitchOkMaxEm, o.PenaltyPitchOutOfRange));
        Assert.Equal((1.6, 0.15), (o.TwoLinePitchMaxEm, o.PenaltyTwoLineWidePitch));
        Assert.Equal((0.08, 0.10), (o.BonusJustified, o.BonusFullLines));
        Assert.Equal((10.0, 0.25, 15.0, 0.10), (o.ShortAvgEm, o.PenaltyShortAvg, o.NarrowAvgEm, o.PenaltyNarrowAvg));
        Assert.Equal((3, 0.10), (o.FewWordsMax, o.PenaltyFewWords));
        Assert.Equal((0.6, 0.25), (o.ListCapitalFrac, o.PenaltyListLike));
        Assert.Equal((4, 0.30), (o.CenterListMinLines, o.PenaltyCenterList));
        Assert.Equal((4.0, 0.3), (o.TinyAvgEm, o.TinyAvgCap));
        Assert.Equal((3, 2, 0.6, 12, 0.55), (o.RepeatedPrefixMinLines, o.RepeatedPrefixMinCount, o.RepeatedPrefixFrac, o.PrefixTokenMaxChars, o.PenaltyRepeatedPrefix));
        Assert.Equal((0.5, 40, 0.5), (o.LabelLineFrac, o.LabelMaxChars, o.PenaltyLabelLines));
        Assert.Equal((0.5, 2.5, 0.12, 4.0), (o.GrowthIgnoreBelowEm, o.ShortLastLineEm, o.DuplicateTolEm, o.ColumnNearEm));
    }


    // ------------------------------------------------------------------ 목록 특징 (Task 2에서 실제 6~10쪽을 보고 추가)
    [Fact]
    public void ParameterList_LabelColonLines_NotMerged()
    {
        string[] t = ["the design pressure : 15.5 MPa (155 bar)", "the working pressure : 13.5 MPa (135 bar)", "the setted low pressure alarm : 11.5 MPa", "the setted high pressure alarm : 15 MPa", "the max. pump discharging pressure : 14 MPa"];
        var lines = Lines(t.Select((s, i) => Run(s, 60, 700 - i * 12, width: 300 - (i * 7 % 15))));
        var m = DetectTop(lines);
        Assert.Single(m.Lines);                                  // 항목 나열: 한 줄 편집
        Assert.Equal(ParagraphReasons.LowConfidence, m.LowConfidenceReason);
    }

    [Fact]
    public void RepeatedPrefixList_NotMerged()
    {
        string[] t = ["2 sets Upper connection block", "2 sets Lower connection block", "2 sets Isolating block for manifold", "4 sets Emergency hand pump connector", "52 sets Flow reducer for valve speed"];
        var m = DetectTop(Lines(t.Select((s, i) => Run(s, 60, 700 - i * 12, width: 300))));
        Assert.Single(m.Lines);
        string[] a = ["ANNEX II rules for control of liquid substances", "ANNEX III prevention of pollution by packaged goods", "ANNEX IV prevention of pollution by sewage ships", "ANNEX V prevention of pollution by garbage ships"];
        Assert.Single(DetectTop(Lines(a.Select((s, i) => Run(s, 60, 700 - i * 12, width: 300)))).Lines);
    }

    [Fact]
    public void IntroLineFollowedByQuantityItems_NotMerged_ButLowercaseRepeatsAreFine()
    {
        string[] t = ["Below listed items are provided on the unit", "1 set - visual level gauge with switch", "1 set - air breather filter"];
        Assert.Single(DetectTop(Lines(t.Select((s, i) => Run(s, 60, 700 - i * 12, width: 300)))).Lines);
        // 줄머리 낱말이 소문자 the/and로 겹치는 보통 문단은 그대로 문단
        string[] p = ["we inspected the valve and found a crack in the", "the weld near the flange that needs repair before", "the system can be started again after testing is"];
        var m = DetectTop(Lines(p.Select((s, i) => Run(s, 60, 700 - i * 12, width: 300))));
        Assert.Equal(3, m.Lines.Length);
    }

    [Fact]
    public void ContactBlock_TelFaxLines_NotMerged()
    {
        string[] t = ["8/F Juyang Tower, No.1200 Pudong Avenue,", "Shanghai, China. PC 200135", "Tel : +86-21-6888-9889", "Fax : +86-21-6329-3888"];
        var m = DetectTop(Lines(t.Select((s, i) => Run(s, 60, 700 - i * 12, width: 220 - i * 10))));
        Assert.Single(m.Lines);
        Assert.Single(DetectTop(Lines([Run("1044D, Ganesh Nagar, Kumbakonam 61240", 60, 700, width: 190), Run("Tel : +91-9843330215", 60, 688, width: 100)])).Lines);
    }

    [Fact]
    public void ListFeatures_DoNotHurtOrdinaryParagraphs()
    {
        // 첫 낱말이 우연히 두 번 같거나(5줄 중 2), 콜론이 한 줄에 있어도 문단은 그대로 묶인다
        var runs = Body(5, 60, 700).ToList();
        Assert.True(DetectTop(Lines(runs)).IsParagraph);
    }

    [Fact]
    public void RepeatedPrefix_And_LabelLine_HelpersBehave()
    {
        Assert.True(ParagraphDetector.IsLabelLine("Tel : +82-51", Opt));
        Assert.True(ParagraphDetector.IsLabelLine("Fax: 123", Opt));
        Assert.False(ParagraphDetector.IsLabelLine("http://example.com is a site", Opt));
        Assert.False(ParagraphDetector.IsLabelLine("this is a long ordinary sentence where the colon comes far too late: yes", Opt));
        Assert.False(ParagraphDetector.IsLabelLine("no colon here", Opt));
    }

    // ------------------------------------------------------------------ 실제 PDF: 새 쪽으로 문턱 재확인 (수치만, 파일명·내용 기록 금지)
    private static string RealStats(PdfiumThread t, int firstPage, int lastPageExclusive, string outName)
    {
        var sb = new StringBuilder();
        var all = new List<(string Label, string Kind, int Page, ParagraphModel M)>();
        foreach (var s in V17ToV21Probes.RealSources())
        {
            int lineTotal = 0, tableRows = 0, pagesRead = 0;
            for (int pg = firstPage; pg < lastPageExclusive; pg++)
            {
                PageData pd;
                try { pd = ParagraphProbe.ReadPdfium(t, s.Path, pg); }
                catch (Exception) { break; }
                pagesRead++;
                var runs = pd.Runs.Select(ToTextRun).ToList();
                var obs = pd.Obstacles.Select(ToObstacle).ToList();
                var lines = LineAssembler.Assemble(runs, obs);
                lineTotal += lines.Length; tableRows += lines.Count(l => l.TableRow);
                foreach (var m in ParagraphDetector.Partition(lines, obs, new ParagraphOptions { Threshold = 0.0 }))
                    if (m.Lines.Length >= 2) all.Add((s.Label, s.Kind, pg + 1, m));
            }
            sb.AppendLine($"{s.Label} ({s.Kind}): pagesRead={pagesRead} lines={lineTotal} tableRows={tableRows}");
        }
        sb.AppendLine($"== 문턱 0에서 2줄 이상 묶음의 신뢰도 분포 (실제 {firstPage + 1}~{lastPageExclusive}쪽)");
        foreach (var kind in new[] { "Document", "Drawing" })
        {
            var ms = all.Where(x => x.Kind == kind).ToList();
            sb.AppendLine($"{kind}: 묶음 {ms.Count}개; 신뢰도 ≥0.80 {ms.Count(x => x.M.Confidence >= 0.8)}, 0.70~0.80 {ms.Count(x => x.M.Confidence is >= 0.7 and < 0.8)}, 0.62~0.70 {ms.Count(x => x.M.Confidence is >= 0.62 and < 0.7)}, 0.50~0.62 {ms.Count(x => x.M.Confidence is >= 0.5 and < 0.62)}, <0.50 {ms.Count(x => x.M.Confidence < 0.5)}");
        }
        // 눈으로 확인용(임시 폴더에만 쓴다, 저장소 문서에는 옮기지 않는다): 문턱 근처 묶음의 줄 수·정렬·신뢰도·줄 앞 36자
        var detail = new StringBuilder();
        foreach (var x in all.Where(x => x.M.Confidence >= 0.45).OrderBy(x => x.Label).ThenBy(x => x.Page))
            detail.AppendLine($"{x.Label} p{x.Page} n={x.M.Lines.Length} conf={x.M.Confidence:0.00} {x.M.Align} :: " + string.Join(" / ", x.M.Lines.Select(l => l.Text[..Math.Min(36, l.Text.Length)])));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), outName + "-summary.txt"), sb.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), outName + "-detail-LOCAL.txt"), detail.ToString());
        return sb.ToString();
    }

    [Fact]
    public void RealPdf_Pages6To10_And_11To15_WritesNumbersOnly()
    {
        // 6~10쪽은 목록 특징을 고치는 데 본 쪽, 11~15쪽은 고친 뒤에 처음 보는 쪽(검증용)
        RealStats(_t, 5, 10, "m5-task2-real-6to10");
        RealStats(_t, 10, 15, "m5-task2-real-11to15");
    }
}
