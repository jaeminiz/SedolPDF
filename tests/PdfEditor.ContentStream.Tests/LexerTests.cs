using System.Text;

namespace PdfEditor.ContentStream.Tests;

public class LexerTests
{
    internal static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>연산자 범위가 순서대로 겹치지 않고, 사이(틈)에는 공백·주석만 있는지 — "범위 보존"의 핵심 성질.</summary>
    internal static void AssertTiles(ParsedContent pc)
    {
        int pos = 0;
        foreach (var op in pc.Ops)
        {
            Assert.True(op.Start >= pos, $"overlap at {op.Name}");
            Assert.True(op.OpStart >= op.Start && op.End > op.OpStart);
            AssertGap(pc.Data, pos, op.Start);
            pos = op.End;
        }
        if (pc.Ok) AssertGap(pc.Data, pos, pc.Data.Length, allowTrailingOperands: true);
    }

    private static void AssertGap(byte[] d, int from, int to, bool allowTrailingOperands = false)
    {
        for (int i = from; i < to; i++)
        {
            byte b = d[i];
            if (b == '%') { while (i < to && d[i] != 10 && d[i] != 13) i++; continue; }
            if (allowTrailingOperands) continue;
            Assert.True(b is 0 or 9 or 10 or 12 or 13 or 32, $"non-blank byte {b} in gap at {i}");
        }
    }

    /// <summary>무편집 왕복: 범위를 이어 붙여 다시 만들면 원본과 바이트 동일.</summary>
    internal static byte[] Reassemble(ParsedContent pc)
    {
        var ms = new MemoryStream(); int pos = 0;
        foreach (var op in pc.Ops)
        {
            ms.Write(pc.Data, pos, op.Start - pos);
            ms.Write(pc.Data, op.Start, op.End - op.Start);
            pos = op.End;
        }
        ms.Write(pc.Data, pos, pc.Data.Length - pos);
        return ms.ToArray();
    }

    [Fact]
    public void SimpleOps_RangesPreserved_RoundTripIdentical()
    {
        var data = B("q 1 0 0 1 10 20 cm\n% comment (not a string\n/F1 12 Tf BT 0 0 Td [(A) -50 (B\\)C)] TJ ET Q\n0.5 g 0 0 100 100 re f");
        var pc = ContentLexer.Parse(data);
        Assert.True(pc.Ok, pc.Failure);
        Assert.Equal(["q", "cm", "Tf", "BT", "Td", "TJ", "ET", "Q", "g", "re", "f"], pc.Ops.Select(o => o.Name));
        Assert.Equal(6, pc.Ops[1].Operands.Count);
        Assert.Equal(10, pc.Ops[1].Operands[4].Number);
        Assert.Equal("F1", pc.Ops[2].Operands[0].Text);
        Assert.Equal(Encoding.Latin1.GetBytes("B)C"), pc.Ops[5].Operands[0].Items[2].Bytes);
        AssertTiles(pc);
        Assert.Equal(data, Reassemble(pc));
        // 연산자 범위가 피연산자를 포함한다
        Assert.Equal("1 0 0 1 10 20 cm", Encoding.Latin1.GetString(data, pc.Ops[1].Start, pc.Ops[1].End - pc.Ops[1].Start));
    }

    [Fact]
    public void Strings_Hex_Octal_Names_Dicts()
    {
        var pc = ContentLexer.Parse(B("<48 65 6C6C6F> Tj (a\\101\\n\\\r\nb) Tj /A#42C gs /Span <</MCID 3 /Lang (en)>> BDC EMC"));
        Assert.True(pc.Ok, pc.Failure);
        Assert.Equal("Hello", Encoding.Latin1.GetString(pc.Ops[0].Operands[0].Bytes));
        Assert.Equal("aA\nb", Encoding.Latin1.GetString(pc.Ops[1].Operands[0].Bytes));
        Assert.Equal("ABC", pc.Ops[2].Operands[0].Text);
        Assert.Equal(OperandKind.Dict, pc.Ops[3].Operands[1].Kind);
        Assert.Equal(4, pc.Ops[3].Operands[1].Items.Count);
        AssertTiles(pc);
    }

    [Fact]
    public void InlineImage_IsOneOperator_BinaryDataSafe()
    {
        var bytes = new List<byte>(B("q 10 0 0 10 5 5 cm BI /W 2 /H 2 /CS /G /BPC 8 ID "));
        bytes.AddRange(new byte[] { 0x00, 0x20, 0x45, 0x49, 0x20, 0xFF });   // 데이터 안에 " EI " 같은 바이트가 있어도(뒤가 평문이 아님)
        bytes.AddRange(B("\nEI Q 0 0 m 5 5 l S"));
        var data = bytes.ToArray();
        var pc = ContentLexer.Parse(data);
        Assert.True(pc.Ok, pc.Failure);
        Assert.Equal(["q", "cm", "BI", "Q", "m", "l", "S"], pc.Ops.Select(o => o.Name));
        var bi = pc.Ops[2];
        Assert.Equal(7, bi.DataEnd - bi.DataStart);   // 6바이트 + EI 앞 줄바꿈
        Assert.Equal(2, bi.Operands[1].Number);
        AssertTiles(pc);
        Assert.Equal(data, Reassemble(pc));
    }

    [Theory]
    [InlineData("(unterminated string Tj")]
    [InlineData("[1 2 3 TJ")]
    [InlineData("<</A 1 gs")]
    [InlineData("1 2 ] m")]
    [InlineData("BI /W 1 /H 1 ID abc")]
    [InlineData("BI /W 1 /H 1 q Q")]
    [InlineData("<4G> Tj")]
    [InlineData("[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[ Tj")]
    public void Malformed_ReturnsFailure_NoThrow(string s)
    {
        var pc = ContentLexer.Parse(B(s));
        Assert.False(pc.Ok);
        Assert.False(string.IsNullOrEmpty(pc.Failure));
    }

    [Fact]
    public void EmptyAndGarbage_AreSafe()
    {
        Assert.True(ContentLexer.Parse([]).Ok);
        Assert.Empty(ContentLexer.Parse(B("  \n % only comment")).Ops);
        var rnd = new Random(7);
        for (int k = 0; k < 300; k++)
        {
            var buf = new byte[rnd.Next(0, 200)];
            rnd.NextBytes(buf);
            var pc = ContentLexer.Parse(buf);   // 예외 없이 끝나야 한다
            AssertTiles(pc);
        }
    }

    [Fact]
    public void TrailingOperandsWithoutOperator_AreIgnoredButBytesKept()
    {
        var data = B("0 0 m 1 2");
        var pc = ContentLexer.Parse(data);
        Assert.True(pc.Ok);
        Assert.Single(pc.Ops);
        Assert.Equal(data, Reassemble(pc));
    }

    [Fact]
    public void Numbers_Forms()
    {
        var pc = ContentLexer.Parse(B("-.5 +3 4. 0.25 -0 w"));
        var n = pc.Ops[0].Operands.Select(o => o.Number).ToArray();
        Assert.Equal([-0.5, 3, 4, 0.25, 0], n);
    }
}
