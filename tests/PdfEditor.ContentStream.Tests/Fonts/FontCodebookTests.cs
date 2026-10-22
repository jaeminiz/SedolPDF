using System.Text;
using PdfEditor.ContentStream.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.ContentStream.Tests.Fonts;

/// <summary>글꼴 사전을 손으로 짜서 코드북 규칙(C8)을 시험한다. PDFsharp만 쓰고 PDFium은 쓰지 않는다.</summary>
public class FontCodebookTests
{
    private static readonly PdfDocument Doc = new();

    internal static PdfDictionary Descriptor(bool embedded = true, double? missingWidth = null)
    {
        var fd = new PdfDictionary(Doc);
        fd.Elements.SetName("/Type", "/FontDescriptor");
        fd.Elements.SetName("/FontName", "/ABCDEF+Test");
        if (missingWidth is { } mw) fd.Elements.SetReal("/MissingWidth", mw);
        if (embedded)
        {
            var ff = new PdfDictionary(Doc);
            ff.CreateStream([1, 2, 3]);
            Doc.Internals.AddObject(ff);
            fd.Elements.SetReference("/FontFile2", ff);
        }
        Doc.Internals.AddObject(fd);
        return fd;
    }

    internal static PdfDictionary ToUnicode(params (int Code, string Text)[] map)
    {
        var sb = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n");
        sb.Append(map.Length).Append(" beginbfchar\n");
        foreach (var (code, text) in map)
            sb.Append('<').Append(code.ToString("X4")).Append("> <").Append(string.Concat(text.Select(c => ((int)c).ToString("X4")))).Append(">\n");
        sb.Append("endbfchar\nendcmap end end\n");
        var tu = new PdfDictionary(Doc);
        tu.CreateStream(Encoding.ASCII.GetBytes(sb.ToString()));
        Doc.Internals.AddObject(tu);
        return tu;
    }

    /// <summary>단순 TrueType 글꼴: 코드 65~90('A'~'Z')와 32(공백)의 폭을 준다.</summary>
    internal static PdfDictionary SimpleFont(bool toUnicode = true, bool embedded = true, bool widths = true, string subtype = "/TrueType")
    {
        var f = new PdfDictionary(Doc);
        f.Elements.SetName("/Type", "/Font");
        f.Elements.SetName("/Subtype", subtype);
        f.Elements.SetName("/BaseFont", "/ABCDEF+Test");
        if (widths)
        {
            f.Elements.SetInteger("/FirstChar", 32);
            var w = new PdfArray(Doc);
            for (int c = 32; c <= 90; c++) w.Elements.Add(new PdfInteger(c == 32 ? 250 : 500 + (c - 65) * 10));
            f.Elements.SetObject("/Widths", w);
        }
        f.Elements.SetReference("/FontDescriptor", Descriptor(embedded));
        if (toUnicode)
        {
            var map = Enumerable.Range(32, 59).Select(c => (c, ((char)c).ToString())).ToArray();
            f.Elements.SetReference("/ToUnicode", ToUnicode(map));
        }
        return f;
    }

    /// <summary>Type0 / Identity-H: CID 1=가, 2=나, 3=공백, 4=A. DW 1000, /W로 3번만 250, 4번 600.</summary>
    internal static PdfDictionary CidFont(bool toUnicode = true, string encoding = "/Identity-H")
    {
        var cid = new PdfDictionary(Doc);
        cid.Elements.SetName("/Type", "/Font");
        cid.Elements.SetName("/Subtype", "/CIDFontType2");
        cid.Elements.SetInteger("/DW", 1000);
        var w = new PdfArray(Doc);
        w.Elements.Add(new PdfInteger(3));
        var inner = new PdfArray(Doc);
        inner.Elements.Add(new PdfInteger(250));
        inner.Elements.Add(new PdfInteger(600));
        w.Elements.Add(inner);
        cid.Elements.SetObject("/W", w);
        cid.Elements.SetReference("/FontDescriptor", Descriptor());
        Doc.Internals.AddObject(cid);

        var f = new PdfDictionary(Doc);
        f.Elements.SetName("/Type", "/Font");
        f.Elements.SetName("/Subtype", "/Type0");
        f.Elements.SetName("/BaseFont", "/ABCDEF+Test");
        f.Elements.SetName("/Encoding", encoding);
        var desc = new PdfArray(Doc);
        desc.Elements.Add(cid.ReferenceNotNull);
        f.Elements.SetObject("/DescendantFonts", desc);
        if (toUnicode) f.Elements.SetReference("/ToUnicode", ToUnicode((1, "가"), (2, "나"), (3, " "), (4, "A")));
        return f;
    }

    private static ReadOnlyMemory<byte>[] Shown(params byte[][] s) => s.Select(x => (ReadOnlyMemory<byte>)x).ToArray();

    [Fact]
    public void Codebook_SimpleFont_WidthsFromWidthsArray()
    {
        var book = FontCodebook.TryBuild(SimpleFont(), Shown("AB C"u8.ToArray()));
        Assert.NotNull(book);
        Assert.False(book!.TwoByte);
        Assert.Equal(500f, book.Width1000('A'));
        Assert.Equal(510f, book.Width1000('B'));
        Assert.Equal(250f, book.Width1000(' '));
        Assert.True(book.TryEncode("A B", out var enc));
        Assert.Equal("A B"u8.ToArray(), enc.Bytes);
        Assert.Equal([500f, 250f, 510f], enc.Widths.ToArray());
        Assert.Equal(1260f, enc.Total1000);
    }

    [Fact]
    public void Codebook_CidIdentityH_ToUnicodeAndW()
    {
        var book = FontCodebook.TryBuild(CidFont(), Shown([0, 1, 0, 3, 0, 2, 0, 4]));
        Assert.NotNull(book);
        Assert.True(book!.TwoByte);
        Assert.True(book.TryEncode("나 가A", out var enc));
        Assert.Equal(new byte[] { 0, 2, 0, 3, 0, 1, 0, 4 }, enc.Bytes);
        Assert.Equal([1000f, 250f, 1000f, 600f], enc.Widths.ToArray());   // 가·나는 /W에 없어 DW
        Assert.Equal("가 나A", book.Decode([0, 1, 0, 3, 0, 2, 0, 4]));
    }

    [Fact]
    public void Codebook_OnlyCharsPresentInParagraph()
    {
        // ToUnicode에는 A~Z가 다 있지만 문단에 보인 글은 A·B뿐 → 다른 글자는 서브셋에 글리프가 있다는 보증이 없다.
        var book = FontCodebook.TryBuild(SimpleFont(), Shown("ABBA"u8.ToArray()))!;
        Assert.True(book.TryEncode("BAAB", out _));
        Assert.False(book.TryEncode("ABC", out _));
        Assert.False(book.TryEncode("A B", out _));    // 공백도 보인 적이 없으면 못 쓴다
        Assert.Equal(new[] { 'A', 'B' }, book.Available.OrderBy(c => c).ToArray());

        var ko = FontCodebook.TryBuild(CidFont(), Shown([0, 1]))!;
        Assert.True(ko.TryEncode("가", out _));
        Assert.False(ko.TryEncode("나", out _));
    }

    [Fact]
    public void Codebook_NoToUnicode_ReturnsNull()
    {
        Assert.Null(FontCodebook.TryBuild(SimpleFont(toUnicode: false), Shown("AB"u8.ToArray())));
        Assert.Null(FontCodebook.TryBuild(CidFont(toUnicode: false), Shown([0, 1])));
        FontCodebook.TryBuild(SimpleFont(toUnicode: false), Shown("AB"u8.ToArray()), null, out var reason);
        Assert.Equal("no-tounicode", reason);
    }

    [Fact]
    public void Codebook_NoToUnicode_LearnsFromPdfiumText()
    {
        var shown = "AB C"u8.ToArray();
        // 코드 수 = 글자 수(공백 포함 1:1)
        var book = FontCodebook.TryBuild(SimpleFont(toUnicode: false), Shown(shown), [(shown, "AB C")], out var reason);
        Assert.NotNull(book);
        Assert.Null(reason);
        Assert.True(book!.TryEncode("CAB", out var enc));
        Assert.Equal("CAB"u8.ToArray(), enc.Bytes);
        // 공백 글자가 PDFium 글에서 빠진 경우(비공백끼리 1:1)
        var book2 = FontCodebook.TryBuild(SimpleFont(toUnicode: false), Shown(shown), [(shown, "ABC")], out _);
        Assert.NotNull(book2);
        Assert.True(book2!.TryEncode("CAB", out _));
        Assert.False(book2.TryEncode("A B", out _));   // 공백 코드는 짝을 알 수 없어 배우지 않는다(대체 글꼴로 간다)
    }

    [Fact]
    public void Codebook_LearnConflict_ReturnsNull()
    {
        var a = "AB"u8.ToArray();
        var book = FontCodebook.TryBuild(SimpleFont(toUnicode: false), Shown(a), [(a, "xy"), (a, "xz")], out var reason);
        Assert.Null(book);
        Assert.Equal("learn-conflict", reason);
    }

    [Fact]
    public void Codebook_Standard14WithoutWidths_ReturnsNull()
    {
        var f = SimpleFont(widths: false, subtype: "/Type1");
        Assert.Null(FontCodebook.TryBuild(f, Shown("AB"u8.ToArray()), null, out var reason));
        Assert.Equal("no-widths", reason);
    }

    [Fact]
    public void Codebook_Type3_ReturnsNull()
    {
        var f = SimpleFont(subtype: "/Type3");
        Assert.Null(FontCodebook.TryBuild(f, Shown("AB"u8.ToArray()), null, out var reason));
        Assert.Equal("type3", reason);
    }

    [Fact]
    public void Codebook_NotEmbedded_And_MixedCodeSpace_ReturnNull()
    {
        Assert.Null(FontCodebook.TryBuild(SimpleFont(embedded: false), Shown("AB"u8.ToArray()), null, out var r1));
        Assert.Equal("not-embedded", r1);
        Assert.Null(FontCodebook.TryBuild(CidFont(encoding: "/UniKS-UCS2-H"), Shown([0, 1]), null, out var r2));
        Assert.Equal("mixed-code-space", r2);
    }

    [Fact]
    public void Codebook_UsedCodeWithoutWidthAndNoMissingWidth_ReturnsNull()
    {
        // 폭 표(32~90) 밖의 코드 'a'(97)가 보였는데 MissingWidth도 없으면 폭을 모른다.
        var f = SimpleFont();
        f.Elements.SetReference("/ToUnicode", ToUnicode((97, "a"), (65, "A")));
        Assert.Null(FontCodebook.TryBuild(f, Shown("Aa"u8.ToArray()), null, out var reason));
        Assert.Equal("width-missing", reason);
    }
}
