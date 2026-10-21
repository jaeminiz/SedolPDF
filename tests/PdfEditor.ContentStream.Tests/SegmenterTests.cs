namespace PdfEditor.ContentStream.Tests;

public class SegmenterTests
{
    private static Segmentation Seg(string s, Func<string, XObjectInfo?>? x = null)
    {
        var pc = ContentLexer.Parse(LexerTests.B(s));
        Assert.True(pc.Ok, pc.Failure);
        return ContentSegmenter.Segment(pc, x);
    }

    private static string Kinds(Segmentation s) => string.Concat(s.Objects.Select(o => o.Kind switch
    { ObjectKind.Text => 'T', ObjectKind.Path => 'P', ObjectKind.Image => 'I', ObjectKind.Shading => 'S', _ => 'F' }));

    [Fact]
    public void V16Rules_EmptyText_N_SinglePoint_ClosedDot()
    {
        var s = Seg("BT /F1 12 Tf () Tj [(A) -50 (B)] TJ (C) ' 1 2 (D) \" 3 Tr (hid) Tj ET " +
                    "10 10 m f " +                  // 점 1개: 객체 아님
                    "0 0 m h S " +                  // 닫힌 한 점: 객체
                    "5 5 m s " +                    // s 한 점: 객체
                    "0 0 10 10 re W n " +           // n: 객체 아님
                    "0 0 m 100 100 l S 20 20 50 50 re B* " +
                    "BI /W 1 /H 1 /CS /G /BPC 8 ID \u0080 EI " +
                    "/Sh sh");
        Assert.Equal("TTTTPPPPIS", Kinds(s));
    }

    [Fact]
    public void DoResolvesKind_UnknownIgnored()
    {
        XObjectInfo? R(string n) => n switch
        {
            "Im" => new XObjectInfo(ObjectKind.Image, null, null),
            "Fm" => new XObjectInfo(ObjectKind.Form, new Mat(1, 0, 0, 1, 5, 5), (0, 0, 10, 20)),
            _ => null,
        };
        var s = Seg("q 2 0 0 2 100 100 cm /Im Do /Fm Do /Nope Do Q", R);
        Assert.Equal("IF", Kinds(s));
        var im = s.Objects[0].Bounds!.Value;
        Assert.Equal(new Box(100, 100, 102, 102), im);
        var fm = s.Objects[1].Bounds!.Value;   // BBox(0,0,10,20) → Matrix(+5,+5) → CTM(x2, +100) = (110..130, 110..150)
        Assert.Equal(new Box(110, 110, 130, 150), fm);
        Assert.Equal("Fm", s.Objects[1].XObjectName);
    }

    [Fact]
    public void Ctm_Stack_And_PathBounds()
    {
        var s = Seg("q 1 0 0 1 10 20 cm q 2 0 0 2 0 0 cm 0 0 5 5 re f Q 0 0 3 3 re f Q 0 0 1 1 re f");
        Assert.Equal(3, s.Objects.Count);
        Assert.Equal(new Box(10, 20, 20, 30), s.Objects[0].Bounds);   // (0..5)*2 + (10,20)
        Assert.Equal(new Box(10, 20, 13, 23), s.Objects[1].Bounds);
        Assert.Equal(new Box(0, 0, 1, 1), s.Objects[2].Bounds);
        Assert.Equal(new Mat(2, 0, 0, 2, 10, 20), s.Objects[0].Ctm);   // 안쪽 cm × 바깥 cm
    }

    [Fact]
    public void PathFirstOp_And_Clip_And_StrokeWidth()
    {
        var s = Seg("3 w 0 0 m 10 0 l 10 10 l S 0 0 5 5 re W f");
        Assert.Equal(2, s.Objects.Count);
        Assert.Equal(1, s.Objects[0].FirstOpIndex);   // `3 w`=0, `0 0 m`=1
        Assert.False(s.Objects[0].HasOwnClip);
        Assert.True(s.Objects[1].HasOwnClip);
        Assert.Equal(3, s.Objects[0].StrokeWidth);
        Assert.Equal(0, s.Objects[1].StrokeWidth);
    }

    [Fact]
    public void Text_Origin_Tm_Snapshot_Movable()
    {
        var s = Seg("q 1 0 0 1 100 100 cm /F1 10 Tf 0.5 g BT 1 0 0 1 20 30 Tm (A) Tj (B) Tj 5 0 Td (C) Tj ET Q");
        Assert.Equal(3, s.Objects.Count);
        var a = s.Objects[0]; var b = s.Objects[1]; var c = s.Objects[2];
        Assert.Equal((120.0, 130.0), a.Origin);
        Assert.True(a.Text!.Movable);
        Assert.Null(b.Origin);                 // 글자 폭을 모르므로 직후 위치는 모른다
        Assert.False(b.Text!.Movable);
        Assert.Equal((125.0, 130.0), c.Origin);   // Td는 줄 행렬 기준이라 Tj 연속과 무관
        Assert.True(a.Text.EtOp > a.OpIndex && a.Text.EtOp == c.Text!.EtOp);
        Assert.Contains(a.Text.ReplayOps, k => k >= 0);   // 색(g)이 재생 목록에 들었다
    }

    [Fact]
    public void TextStateOps_QuoteAndDoubleQuote_And_CmInText()
    {
        var s = Seg("BT /F1 10 Tf 12 TL 0 100 Td (a) ' 1 2 (b) \" ET BT /F1 8 Tf 0 0 Td 2 0 0 2 0 0 cm (c) Tj ET");
        Assert.Equal(3, s.Objects.Count);
        Assert.Equal((0.0, 88.0), s.Objects[0].Origin);
        Assert.Equal((0.0, 76.0), s.Objects[1].Origin);
        Assert.Equal(1, s.Objects[1].Text!.Tw);
        Assert.Equal(2, s.Objects[1].Text!.Tc);
        Assert.False(s.Objects[2].Text!.Movable);    // BT 안의 cm
    }

    [Fact]
    public void BadOperands_Fail_ParseFailure_Propagates()
    {
        var pc = ContentLexer.Parse(LexerTests.B("1 0 0 cm"));
        var seg = ContentSegmenter.Segment(pc);
        Assert.False(seg.Ok);
        var bad = ContentLexer.Parse(LexerTests.B("(abc"));
        Assert.False(ContentSegmenter.Segment(bad).Ok);
    }

    [Fact]
    public void UnbalancedQ_IsTolerated()
    {
        var s = Seg("Q Q 0 0 5 5 re f q q 0 0 1 1 re f");
        Assert.Equal("PP", Kinds(s));
    }
}
