using System.Text;

namespace PdfEditor.ContentStream.Tests;

public class ContentRepairTests
{
    private static string Str(byte[] d) => Encoding.Latin1.GetString(d);

    [Fact]
    public void UnmatchedRestore_IsRemoved_RestKept()
    {
        // 2026-11-10 사용자 PDF 모양: 다시 쓴 앞 스트림이 걸친 q를 빠뜨려 뒤에 짝 없는 Q가 남음
        var (data, removed) = ContentRepair.RemoveUnmatchedRestores(LexerTests.B("q 1 0 0 RG 10 10 m 100 10 l S Q\n.75 0 0 -.75 0 841 cm\n1 J 0.5 w 20 50 m 200 50 l S Q\nq 0 1 0 rg 300 600 80 80 re f Q\n"));
        Assert.Equal(1, removed);
        var pc = ContentLexer.Parse(data!);
        Assert.True(pc.Ok, pc.Failure);
        Assert.Equal(2, pc.Ops.Count(o => o.Name == "Q"));
        Assert.Equal(2, pc.Ops.Count(o => o.Name == "q"));
        Assert.Contains("200 50 l S", Str(data!));
        Assert.Contains("80 80 re f Q", Str(data!));
    }

    [Fact]
    public void Balanced_ReturnsNull()
        => Assert.Equal((null, 0), ContentRepair.RemoveUnmatchedRestores(LexerTests.B("q 1 0 0 RG 10 10 m 100 10 l S Q q Q")));

    [Fact]
    public void QInsideString_IsNotAnOperator()
    {
        var (data, removed) = ContentRepair.RemoveUnmatchedRestores(LexerTests.B("BT /F1 12 Tf (Q) Tj ET Q"));
        Assert.Equal(1, removed);
        Assert.Contains("(Q) Tj", Str(data!));
    }

    /// <summary>2026-11-10 사용자 PDF 4쪽 모양(spec 3.1).</summary>
    private static List<byte[]> Page4Like() =>
    [
        LexerTests.B("q"),
        LexerTests.B("q q"),
        LexerTests.B("q 0 0 0 RG 1 w 10 10 m 20 10 l S Q .75 0 0 -.75 0 841.92 cm"),
        LexerTests.B("S Q q 0 0 0 RG 1 J 30 30 m 40 30 l S Q q 0 0 0 RG"),
        LexerTests.B("1 w 50 50 m 60 50 l S Q q 0 0 0 RG 70 70 m 80 70 l S Q q"),
        LexerTests.B("0.32 w 90 90 m 95 95 l S Q Q"),
        LexerTests.B("Q q q BT /SedolF1 12 Tf (A) Tj ET Q Q Q"),
        LexerTests.B("Q q q BT /SedolF2 12 Tf (B) Tj ET Q Q"),   // spec 3.1 표의 s7: 증감 −1
    ];

    private static (int Min, int End) Depth(byte[] data)
    {
        int d = 0, min = 0;
        foreach (var o in ContentLexer.Parse(data).Ops)
        {
            if (o.Name == "q") d++;
            else if (o.Name == "Q") { d--; min = Math.Min(min, d); }
        }
        return (min, d);
    }

    [Fact]
    public void Page4_RemovesOnlyTailFragmentOfStream3()
    {
        var (data, fragments, _) = ContentRepair.RepairStreams(Page4Like());
        Assert.Equal(1, fragments);
        string s = Str(data!);
        Assert.DoesNotContain("cm\nS Q", s);
        Assert.Contains("841.92 cm", s);
        Assert.Contains("30 30 m 40 30 l S Q", s);
        Assert.Contains("90 90 m 95 95 l S Q", s);
        Assert.Equal((0, 0), Depth(data!));
    }

    [Fact]
    public void Page4_StreamSevenLeadingQ_RemovedByWholePageRule()
    {
        var (data, _, restores) = ContentRepair.RepairStreams(Page4Like());
        Assert.Equal(1, restores);
        Assert.Contains("(A) Tj", Str(data!));
        Assert.Contains("(B) Tj", Str(data!));
    }

    [Fact]
    public void BareQPrefix_IsWrapperClose_Kept()
        => Assert.Equal((null, 0, 0), ContentRepair.RepairStreams([LexerTests.B("q"), LexerTests.B("0 0 1 rg 0 0 5 5 re f"), LexerTests.B("Q q 1 0 0 rg 9 9 5 5 re f Q")]));

    [Fact]
    public void LegitStraddle_Kept()
        => Assert.Equal((null, 0, 0), ContentRepair.RepairStreams([LexerTests.B("q 1 0 0 RG 1 w 10 10 m"), LexerTests.B("20 10 l S Q")]));

    [Theory]
    [InlineData("q 1 0 0 RG 10 10 m 100 10 l S Q\n.75 0 0 -.75 0 841 cm\n1 J 0.5 w 20 50 m 200 50 l S Q\nq 0 1 0 rg 300 600 80 80 re f Q\n")]
    [InlineData("q 1 0 0 RG 10 10 m 100 10 l S Q q Q")]
    [InlineData("BT /F1 12 Tf (Q) Tj ET Q")]
    public void SingleStream_SameAsWholePageRule(string body)
    {
        var whole = ContentRepair.RemoveUnmatchedRestores(LexerTests.B(body + "\n"));
        var (data, fragments, restores) = ContentRepair.RepairStreams([LexerTests.B(body)]);
        Assert.Equal(0, fragments);
        Assert.Equal(whole.Removed, restores);
        if (whole.Data is null) Assert.Null(data); else Assert.Equal(whole.Data, data);
    }

    [Fact]
    public void UnparsableStream_FallsBackToWholePageRule()
    {
        var streams = new List<byte[]> { LexerTests.B("q 1 0 0 RG"), LexerTests.B("(unterminated"), LexerTests.B("Q Q") };
        var joined = Encoding.Latin1.GetBytes("q 1 0 0 RG\n(unterminated\nQ Q\n");
        var expected = ContentRepair.RemoveUnmatchedRestores(joined);
        var (data, fragments, restores) = ContentRepair.RepairStreams(streams);
        Assert.Equal(0, fragments);
        Assert.Equal(expected.Removed, restores);
        Assert.Equal(expected.Data, data);
    }
}
