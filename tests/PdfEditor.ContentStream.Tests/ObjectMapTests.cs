namespace PdfEditor.ContentStream.Tests;

public class ObjectMapTests
{
    private static Segmentation Seg(string s, Func<string, XObjectInfo?>? x = null) =>
        ContentSegmenter.Segment(ContentLexer.Parse(LexerTests.B(s)), x);

    private static ReferenceObject P(double l, double b, double r, double t) => new(ObjectKind.Path, new Box(l, b, r, t));

    [Fact]
    public void Matching_List_Verifies_And_Indexes()
    {
        var seg = Seg("0 0 10 10 re f 20 20 m 30 30 l S");
        var res = ObjectMap.Verify(seg, [P(0, 0, 10, 10), P(20, 20, 30, 30)]);
        Assert.True(res.Ok, res.Reason);
        Assert.Equal(2, res.BoundsChecked);
        Assert.Equal(ObjectKind.Path, res[1].Kind);
    }

    [Fact]
    public void CountMismatch_Rejected_And_IndexAccessThrows()
    {
        var seg = Seg("0 0 10 10 re f");
        var res = ObjectMap.Verify(seg, [P(0, 0, 10, 10), P(0, 0, 1, 1)]);
        Assert.False(res.Ok);
        Assert.Throws<InvalidOperationException>(() => res[0]);
    }

    [Fact]
    public void KindMismatch_Rejected_WithIndex()
    {
        var seg = Seg("0 0 10 10 re f /Sh sh");
        var res = ObjectMap.Verify(seg, [P(0, 0, 10, 10), P(0, 0, 10, 10)]);
        Assert.False(res.Ok);
        Assert.Equal(1, res.FirstMismatch);
    }

    [Fact]
    public void ShiftedBounds_Rejected_SmallRoundingAccepted()
    {
        var seg = Seg("0 0 100 100 re f");
        Assert.True(ObjectMap.Verify(seg, [P(-0.4, -0.4, 100.6, 100.4)]).Ok);
        Assert.False(ObjectMap.Verify(seg, [P(30, 0, 130, 100)]).Ok);
    }

    [Fact]
    public void StrokedPath_AllowsPdfiumGrowth_ButNotShrinkOrShift()
    {
        var seg = Seg("1 w 100 100 m 101 100 l S");
        Assert.True(ObjectMap.Verify(seg, [P(60, 60, 140, 140)]).Ok);      // 뾰족한 선 부풀림
        Assert.False(ObjectMap.Verify(seg, [P(300, 300, 301, 301)]).Ok);  // 엉뚱한 곳
    }

    [Fact]
    public void Text_FarFromBox_Rejected_NearAccepted()
    {
        var seg = Seg("BT /F1 10 Tf 1 0 0 1 100 100 Tm (A) Tj ET");
        Assert.True(ObjectMap.Verify(seg, [new ReferenceObject(ObjectKind.Text, new Box(100, 102, 106, 108))]).Ok);   // 잉크가 기준선 위
        Assert.False(ObjectMap.Verify(seg, [new ReferenceObject(ObjectKind.Text, new Box(400, 400, 406, 408))]).Ok);
    }

    [Fact]
    public void SegmentationFailure_Rejected()
    {
        var seg = ContentSegmenter.Segment(ContentLexer.Parse(LexerTests.B("(abc")));
        Assert.False(ObjectMap.Verify(seg, []).Ok);
    }
}
