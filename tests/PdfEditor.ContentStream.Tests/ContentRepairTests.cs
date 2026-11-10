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
}
