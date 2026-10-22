using System.Text;
using PdfEditor.ContentStream.Fonts;

namespace PdfEditor.ContentStream.Tests.Fonts;

public class TextLineWriterTests
{
    private static (FontCodebook Book, EncodedText Line) Simple(string text)
    {
        var book = FontCodebook.TryBuild(FontCodebookTests.SimpleFont(), [(ReadOnlyMemory<byte>)Encoding.ASCII.GetBytes(text)])!;
        Assert.True(book.TryEncode(text, out var enc));
        return (book, enc);
    }

    private static List<ContentOp> Parse(byte[] bytes)
    {
        var pc = ContentLexer.Parse(bytes);
        Assert.True(pc.Ok, pc.Failure);
        return pc.Ops;
    }

    private static ContentOp Op(List<ContentOp> ops, string name) => ops.Single(o => o.Name == name);

    [Fact]
    public void Writer_EmitsTcTzTj_ParsesBack()
    {
        var (book, line) = Simple("AB C");
        var bytes = TextLineWriter.Write(book, "F7", line, new TextLineStyle(12.5f, Tc: 0.4f, Tz: 90, Ts: 1, RenderMode: 2,
            FillOps: "0.2 g", StrokeOps: "0.2 G", StrokeWidth: 0.375f), new Mat(0, 1, -1, 0, 100, 200));
        var ops = Parse(bytes);

        Assert.Equal(0.4, Op(ops, "Tc").Operands[0].Number, 6);
        Assert.Equal(90, Op(ops, "Tz").Operands[0].Number);
        Assert.Equal(1, Op(ops, "Ts").Operands[0].Number);
        Assert.Equal(2, Op(ops, "Tr").Operands[0].Number);
        Assert.Equal(0.375, Op(ops, "w").Operands[0].Number, 6);
        var tf = Op(ops, "Tf");
        Assert.Equal("F7", tf.Operands[0].Text);
        Assert.Equal(12.5, tf.Operands[1].Number);
        var tm = Op(ops, "Tm").Operands.Select(o => o.Number).ToArray();
        Assert.Equal([0, 1, -1, 0, 100, 200], tm);
        Assert.Equal("AB C"u8.ToArray(), Op(ops, "Tj").Operands[0].Bytes);
        Assert.Contains(ops, o => o.Name == "g");
        Assert.Contains(ops, o => o.Name == "G");
    }

    [Fact]
    public void Writer_TwoByteFont_WritesBigEndianCodes()
    {
        var book = FontCodebook.TryBuild(FontCodebookTests.CidFont(), [(ReadOnlyMemory<byte>)new byte[] { 0, 1, 0, 3, 0, 2 }])!;
        Assert.True(book.TryEncode("가 나", out var enc));
        var ops = Parse(TextLineWriter.Write(book, "F1", enc, new TextLineStyle(10), Mat.Identity));
        Assert.Equal(new byte[] { 0, 1, 0, 3, 0, 2 }, Op(ops, "Tj").Operands[0].Bytes);
    }

    [Fact]
    public void Writer_JustifyExtra_UsesTJNumbers()
    {
        var (book, line) = Simple("AB C AB");
        var ops = Parse(TextLineWriter.Write(book, "F1", line, new TextLineStyle(10, Tz: 50), Mat.Identity, justifyExtraAtSpaces: 3f));
        Assert.DoesNotContain(ops, o => o.Name == "Tj");
        Assert.DoesNotContain(ops, o => o.Name == "Tw");
        var arr = Op(ops, "TJ").Operands[0].Items;
        var strings = arr.Where(i => i.Kind == OperandKind.String).Select(i => Encoding.ASCII.GetString(i.Bytes)).ToArray();
        var nums = arr.Where(i => i.Kind == OperandKind.Number).Select(i => i.Number).ToArray();
        Assert.Equal(["AB ", "C ", "AB"], strings);
        Assert.Equal(2, nums.Length);                                   // 공백 두 개 뒤 각각
        // 글 공간 이동량 3 = -adj/1000 * size * Tz/100 → adj = -3*1000/(10*0.5) = -600
        Assert.All(nums, n => Assert.Equal(-600, n, 3));
        // 전체 글자는 그대로
        Assert.Equal("AB C AB", string.Concat(strings));
    }

    [Fact]
    public void Writer_JustifyExtra_NoTrailingSpaceAdjustment_And_NoSpacesMeansTj()
    {
        var (book, line) = Simple("AB ");
        var ops = Parse(TextLineWriter.Write(book, "F1", line, new TextLineStyle(10), Mat.Identity, justifyExtraAtSpaces: 2f));
        var arr = Op(ops, "TJ").Operands[0].Items;
        Assert.DoesNotContain(arr, i => i.Kind == OperandKind.Number);   // 끝 공백 뒤에는 이을 글이 없다

        var (b2, l2) = Simple("ABAB");
        var ops2 = Parse(TextLineWriter.Write(b2, "F1", l2, new TextLineStyle(10), Mat.Identity, justifyExtraAtSpaces: 2f));
        Assert.Contains(ops2, o => o.Name == "Tj");
    }

    [Fact]
    public void Writer_Output_BalancedQ_BT()
    {
        var (book, line) = Simple("AB C");
        foreach (float? j in new float?[] { null, 2f })
        {
            var ops = Parse(TextLineWriter.Write(book, "F1", line, new TextLineStyle(10, FillOps: "1 0 0 rg"), Mat.Translate(5, 6), j));
            Assert.Equal("q", ops[0].Name);
            Assert.Equal("Q", ops[^1].Name);
            Assert.Single(ops, o => o.Name == "BT");
            Assert.Single(ops, o => o.Name == "ET");
            Assert.True(ops.FindIndex(o => o.Name == "BT") < ops.FindIndex(o => o.Name == "Tm"));
            Assert.True(ops.FindIndex(o => o.Name == "ET") < ops.Count - 1);
            Assert.Equal(1, ops.Count(o => o.Name == "q"));
        }
    }

    [Fact]
    public void Writer_FontNameWithSpecialChars_IsEscaped()
    {
        var (book, line) = Simple("AB");
        var ops = Parse(TextLineWriter.Write(book, "F 1/x", line, new TextLineStyle(10), Mat.Identity));
        Assert.Equal("F 1/x", Op(ops, "Tf").Operands[0].Text);
    }

    [Fact]
    public void ContentStream_DoesNotReferencePdfium()
    {
        var asm = typeof(FontCodebook).Assembly;
        var names = asm.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        Assert.DoesNotContain(names, n => n.Contains("Pdf.Native", StringComparison.OrdinalIgnoreCase) || n.Contains("pdfium", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n is "PdfEditor.Document" or "PdfEditor.TextEdit" or "PdfEditor.Appearance");
    }
}
