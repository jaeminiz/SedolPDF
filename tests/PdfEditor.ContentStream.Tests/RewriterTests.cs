using System.Text;

namespace PdfEditor.ContentStream.Tests;

public class RewriterTests
{
    private static (ParsedContent Pc, Segmentation Seg) Load(string s, Func<string, XObjectInfo?>? x = null)
    {
        var pc = ContentLexer.Parse(LexerTests.B(s));
        Assert.True(pc.Ok, pc.Failure);
        var seg = ContentSegmenter.Segment(pc, x);
        Assert.True(seg.Ok, seg.Failure);
        return (pc, seg);
    }

    private static string Str(byte[] d) => Encoding.Latin1.GetString(d);

    private static Segmentation Reseg(RewriteResult r, Func<string, XObjectInfo?>? x = null)
    {
        Assert.True(r.Ok, r.Failure);
        var pc = ContentLexer.Parse(r.Data!);
        Assert.True(pc.Ok, pc.Failure);
        return ContentSegmenter.Segment(pc, x);
    }

    private static void AssertBox(Box expected, Box? actual, double eps = 1e-4)
    {
        Assert.NotNull(actual);
        var a = actual!.Value;
        Assert.True(Math.Abs(expected.L - a.L) < eps && Math.Abs(expected.B - a.B) < eps && Math.Abs(expected.R - a.R) < eps && Math.Abs(expected.T - a.T) < eps,
            $"expected {expected} got {a}");
    }

    private static Box Moved(Box b, Mat t)
    {
        var pts = new[] { t.Apply(b.L, b.B), t.Apply(b.R, b.B), t.Apply(b.L, b.T), t.Apply(b.R, b.T) };
        return new Box(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
    }

    [Fact]
    public void NoEdits_ReturnsIdenticalBytes()
    {
        var (pc, seg) = Load("q 1 0 0 1 5 5 cm 0 0 10 10 re f Q BT /F1 9 Tf (a) Tj ET");
        var r = ContentRewriter.Rewrite(pc, seg, []);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal(pc.Data, r.Data);
    }

    [Fact]
    public void DisablePath_ReplacesPaintWithN_RestUntouched()
    {
        string src = "0.5 g\n0 0 10 10 re W f\n1 0 0 rg 20 20 5 5 re f\n";
        var (pc, seg) = Load(src);
        var r = ContentRewriter.Rewrite(pc, seg, [new DisableObject(0)]);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal("0.5 g\n0 0 10 10 re W n\n1 0 0 rg 20 20 5 5 re f\n", Str(r.Data!));
        Assert.Single(Reseg(r).Objects);
    }

    [Fact]
    public void DisableImageFormShading_AreRemoved()
    {
        XObjectInfo? R(string n) => n == "Im" ? new XObjectInfo(ObjectKind.Image, null, null) : n == "Fm" ? new XObjectInfo(ObjectKind.Form, null, (0, 0, 1, 1)) : null;
        var (pc, seg) = Load("q 10 0 0 10 0 0 cm /Im Do Q /Fm Do /Sh sh BI /W 1 /H 1 /CS /G /BPC 8 ID \u0080 EI 0 0 m 5 5 l S", R);
        Assert.Equal(5, seg.Objects.Count);
        var r = ContentRewriter.Rewrite(pc, seg, [new DisableObject(0), new DisableObject(1), new DisableObject(2), new DisableObject(3)], null, R);
        Assert.True(r.Ok, r.Failure);
        var re = Reseg(r, R);
        Assert.Single(re.Objects);
        Assert.Equal(ObjectKind.Path, re.Objects[0].Kind);
    }

    [Fact]
    public void DisableText_KeepsOperatorsAndAdvance_ViaInvisibleRenderMode()
    {
        var (pc, seg) = Load("BT /F1 10 Tf (A) Tj 2 Tr (B) Tj ET");
        var r = ContentRewriter.Rewrite(pc, seg, [new DisableObject(0), new DisableObject(1)]);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal("BT /F1 10 Tf 3 Tr (A) Tj 0 Tr 2 Tr 3 Tr (B) Tj 2 Tr ET", Str(r.Data!));
        Assert.Equal(2, Reseg(r).Objects.Count(o => o.Kind == ObjectKind.Text));
    }

    [Fact]
    public void DisableText_ClippingRenderMode_Refused()
    {
        var (pc, seg) = Load("BT /F1 10 Tf 7 Tr (A) Tj ET");
        Assert.False(ContentRewriter.Rewrite(pc, seg, [new DisableObject(0)]).Ok);
    }

    [Theory]
    [InlineData("0 0 10 10 re f")]
    [InlineData("q 2 0 0 2 100 50 cm 0 0 10 10 re f Q")]
    [InlineData("q 0 1 -1 0 300 20 cm 5 5 10 20 re 0 0 m S Q")]            // 90° 회전 쪽
    [InlineData("q 0.5 0 0 -0.5 10 800 cm 100 100 40 60 re f Q")]          // y 뒤집힘(CAD 도면식)
    [InlineData("q 1 0 0 1 3 4 cm q 3 1 1 2 0 0 cm 7 8 20 30 re f Q Q")]  // 기울임이 섞인 중첩 CTM
    public void TransformPath_MovesExactlyByThen(string src)
    {
        var (pc, seg) = Load(src);
        int idx = seg.Objects.Count - 1;
        var then = new Mat(1, 0, 0, 1, 33, -17).Then(Mat.Scale(1.5, 0.5));   // 이동 + 크기
        var r = ContentRewriter.Rewrite(pc, seg, [new TransformObject(idx, then)]);
        var re = Reseg(r);
        Assert.Equal(seg.Objects.Count, re.Objects.Count);
        AssertBox(Moved(seg.Objects[idx].Bounds!.Value, then), re.Objects[idx].Bounds);
        for (int i = 0; i < idx; i++) AssertBox(seg.Objects[i].Bounds!.Value, re.Objects[i].Bounds);   // 다른 객체는 그대로
    }

    [Fact]
    public void TransformImageFormInline_Moves()
    {
        XObjectInfo? R(string n) => n == "Im" ? new XObjectInfo(ObjectKind.Image, null, null) : new XObjectInfo(ObjectKind.Form, null, (0, 0, 50, 50));
        var (pc, seg) = Load("q 30 0 0 20 100 100 cm /Im Do Q q 1 0 0 1 200 200 cm /Fm Do Q q 8 0 0 8 10 10 cm BI /W 1 /H 1 /CS /G /BPC 8 ID \u0080 EI Q", R);
        var then = Mat.Translate(5, 6);
        var r = ContentRewriter.Rewrite(pc, seg, [new TransformObject(0, then), new TransformObject(1, then), new TransformObject(2, then)], null, R);
        var re = Reseg(r, R);
        for (int i = 0; i < 3; i++) AssertBox(Moved(seg.Objects[i].Bounds!.Value, then), re.Objects[i].Bounds);
    }

    [Fact]
    public void TransformText_HidesOriginal_AndRedrawsMovedCopyAfterEt()
    {
        string src = "q 1 0 0 1 100 100 cm 0 1 0 rg BT /F1 12 Tf 2 Tc 1 0 0 1 20 30 Tm (Hello) Tj ET Q 0 0 5 5 re f";
        var (pc, seg) = Load(src);
        var then = Mat.Translate(40, -10);
        var r = ContentRewriter.Rewrite(pc, seg, [new TransformObject(0, then)]);
        var re = Reseg(r);
        var texts = re.Objects.Where(o => o.Kind == ObjectKind.Text).ToList();
        Assert.Equal(2, texts.Count);
        Assert.Equal(3, texts[0].Text!.Tr);                                  // 원본은 보이지 않음
        Assert.Equal(0, texts[1].Text!.Tr);
        Assert.Equal((120.0, 130.0), texts[0].Origin);
        Assert.Equal((160.0, 120.0), texts[1].Origin);                       // 옮긴 복사본
        Assert.Equal(2, texts[1].Text!.Tc);                                  // 글 상태 재현
        Assert.Contains("0 1 0 rg", Str(r.Data!));                           // 색 재현
        Assert.Equal(ObjectKind.Path, re.Objects[^1].Kind);                  // 뒤의 경로는 그대로
    }

    [Fact]
    public void TransformText_QuoteOperators_UseTjCopy()
    {
        var (pc, seg) = Load("BT /F1 10 Tf 12 TL 0 100 Td (a) ' ET");
        var r = ContentRewriter.Rewrite(pc, seg, [new TransformObject(0, Mat.Translate(10, 0))]);
        var re = Reseg(r);
        Assert.Equal((10.0, 88.0), re.Objects[1].Origin);
    }

    [Fact]
    public void Refusals()
    {
        // 특이 CTM
        var (pc, seg) = Load("q 0 0 0 0 5 5 cm 0 0 10 10 re f Q");
        Assert.False(ContentRewriter.Rewrite(pc, seg, [new TransformObject(0, Mat.Translate(1, 1))]).Ok);
        Assert.True(ContentRewriter.Rewrite(pc, seg, [new DisableObject(0)]).Ok);   // 끄기는 CTM이 필요 없음

        // 클립을 같이 하는 경로
        (pc, seg) = Load("0 0 10 10 re W f");
        Assert.False(ContentRewriter.Rewrite(pc, seg, [new TransformObject(0, Mat.Translate(1, 1))]).Ok);

        // 경로 중간의 상태 연산자
        (pc, seg) = Load("0 0 m 1 w 5 5 l S");
        Assert.False(ContentRewriter.Rewrite(pc, seg, [new TransformObject(0, Mat.Translate(1, 1))]).Ok);

        // 글 뒤에 위치를 모르는 글 / BT 안 cm
        (pc, seg) = Load("BT /F1 9 Tf (a) Tj (b) Tj ET");
        Assert.False(ContentRewriter.Rewrite(pc, seg, [new TransformObject(1, Mat.Translate(1, 1))]).Ok);
        (pc, seg) = Load("BT /F1 9 Tf (a) Tj 2 0 0 2 0 0 cm ET");
        Assert.False(ContentRewriter.Rewrite(pc, seg, [new TransformObject(0, Mat.Translate(1, 1))]).Ok);

        // 번호 범위·중복
        (pc, seg) = Load("0 0 m 5 5 l S");
        Assert.False(ContentRewriter.Rewrite(pc, seg, [new DisableObject(3)]).Ok);
        Assert.False(ContentRewriter.Rewrite(pc, seg, [new DisableObject(0), new TransformObject(0, Mat.Identity)]).Ok);
    }

    [Fact]
    public void UnbalancedOriginal_StaysUnbalancedTheSame()
    {
        var (pc, seg) = Load("Q q q 0 0 m 5 5 l S");
        var r = ContentRewriter.Rewrite(pc, seg, [new TransformObject(0, Mat.Translate(2, 2))]);
        Assert.True(r.Ok, r.Failure);   // 균형 프로필(끝 깊이·최저 깊이)이 원본과 같다
    }

    [Fact]
    public void Append_Isolates_PageLevelState_That_Original_Leaves_Behind()
    {
        // q/Q 없이 cm·gs를 남기는 원본(쪽 전체 투명도·혼합 모드 등): 덧붙인 새 객체는 기본 상태에서 그려져야 한다
        var (pc, _) = Load("2 0 0 2 10 10 cm /G1 gs 0 0 m 5 5 l S");
        var r = ContentRewriter.AppendOnly(pc, [LexerTests.B("0 0 3 3 re f")]);
        Assert.True(r.Ok, r.Failure);
        var re = Reseg(r);
        Assert.Equal(Mat.Identity, re.Objects[^1].Ctm);
        Assert.Equal(new Mat(2, 0, 0, 2, 10, 10), re.Objects[0].Ctm);   // 원본 객체는 그대로
        // 원본이 q를 더 많이 닫는 파손: 거절
        var (broken, _) = Load("Q 0 0 m 5 5 l S");
        Assert.False(ContentRewriter.AppendOnly(broken, [LexerTests.B("0 0 3 3 re f")]).Ok);
    }

    [Fact]
    public void Append_Clean_And_Dirty()
    {
        var (pc, _) = Load("0 0 m 5 5 l S");
        var r = ContentRewriter.AppendOnly(pc, [LexerTests.B("1 0 0 rg 0 0 3 3 re f")]);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal(2, Reseg(r).Objects.Count);

        // q와 BT가 열린 채 끝나는 원본: 덧붙인 조각이 그 상태를 이어받으면 안 된다
        var (dirty, _) = Load("q 2 0 0 2 0 0 cm BT /F1 9 Tf 5 Tr (x) Tj");
        var r2 = ContentRewriter.AppendOnly(dirty, [LexerTests.B("0 0 3 3 re f")]);
        Assert.True(r2.Ok, r2.Failure);
        var re = Reseg(r2);
        Assert.Equal(ObjectKind.Path, re.Objects[^1].Kind);
        Assert.Equal(Mat.Identity, re.Objects[^1].Ctm);
    }

    [Fact]
    public void RandomEdits_AlwaysParseAndCountMatches()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 60; i++)
        {
            sb.Append($"q 1 0 0 1 {i} {i * 2} cm {i % 3} g ");
            sb.Append((i % 4) switch
            {
                0 => "0 0 5 5 re f ",
                1 => "0 0 m 9 9 l S ",
                2 => "BT /F1 8 Tf 1 0 0 1 3 3 Tm (t) Tj ET ",
                _ => "0 0 4 4 re B* ",
            });
            sb.Append("Q\n");
        }
        var (pc, seg) = Load(sb.ToString());
        var rnd = new Random(3);
        for (int round = 0; round < 40; round++)
        {
            var edits = new List<ObjectEdit>();
            for (int i = 0; i < seg.Objects.Count; i++)
            {
                int k = rnd.Next(4);
                if (k == 1) edits.Add(new DisableObject(i));
                else if (k == 2) edits.Add(new TransformObject(i, Mat.Translate(rnd.Next(-9, 9), rnd.Next(-9, 9))));
            }
            var r = ContentRewriter.Rewrite(pc, seg, edits);
            Assert.True(r.Ok, r.Failure);
        }
    }
}

public class RewriterPerformanceTests
{
    [Fact]
    public void BigDrawing_200kObjects_ParseSegmentRewrite_Fast()
    {
        var sb = new System.Text.StringBuilder(14_000_000);
        for (int i = 0; i < 200_000; i++)
        {
            int x = i % 1000, y = i / 1000;
            if (i % 5 == 0) sb.Append($"BT /F1 6 Tf {x} {y} Td (T{i}) Tj ET\n");
            else sb.Append($"q 1 0 0 1 {x} {y} cm 0.5 w 0 0 m 10 {i % 7 + 1} l S Q\n");
        }
        var data = System.Text.Encoding.Latin1.GetBytes(sb.ToString());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pc = ContentLexer.Parse(data); long tParse = sw.ElapsedMilliseconds;
        var seg = ContentSegmenter.Segment(pc, null); long tSeg = sw.ElapsedMilliseconds;
        var res = ContentRewriter.Rewrite(pc, seg, [new DisableObject(5), new TransformObject(100_000, Mat.Translate(3, 4))]);
        sw.Stop();
        Assert.True(pc.Ok && seg.Ok && res.Ok, res.Failure);
        Assert.Equal(200_000, seg.Objects.Count);
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "m4x-perf.txt"), $"{data.Length} bytes, 200000 objects: {sw.ElapsedMilliseconds} ms (parse {tParse}, +segment {tSeg - tParse}, +rewrite {sw.ElapsedMilliseconds - tSeg})");
        Assert.True(sw.ElapsedMilliseconds < 10000, $"{sw.ElapsedMilliseconds} ms");
    }
}
