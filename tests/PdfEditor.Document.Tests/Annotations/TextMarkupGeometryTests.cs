using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Document.Tests.Annotations;

public class TextMarkupGeometryTests
{
    private static PdfRectF R(float l, float b, float r, float t) => new(l, b, r, t);

    [Fact]
    public void SameLine_WordsAndGaps_MergeIntoOneRect()
    {
        // 낱말 두 개와 그 사이 빈 자리(공백 글자 상자가 없다): 하나로 이어진다.
        var m = TextMarkupGeometry.MergeLines([R(10, 700, 50, 716), R(62, 700, 100, 716), R(112, 700, 160, 716)]);
        Assert.Equal(R(10, 700, 160, 716), Assert.Single(m));
    }

    [Fact]
    public void SameLine_MixedHeights_UnionIsConstantLineHeight()
    {
        // 영문(큰 상자)과 한글(작은 상자)이 한 줄: 줄 높이는 위아래 최대 범위.
        var m = TextMarkupGeometry.MergeLines([R(10, 698, 50, 720), R(53, 702, 70, 716), R(73, 702, 90, 716), R(100, 698, 140, 720)]);
        Assert.Equal(R(10, 698, 140, 720), Assert.Single(m));
    }

    [Fact]
    public void TwoLines_StaySeparate_OneRectEach()
    {
        var m = TextMarkupGeometry.MergeLines([R(10, 740, 100, 760), R(110, 740, 200, 760), R(10, 712, 80, 732), R(90, 712, 150, 732)]);
        Assert.Equal(2, m.Count);
        Assert.Equal(R(10, 740, 200, 760), m[0]);
        Assert.Equal(R(10, 712, 150, 732), m[1]);
    }

    [Fact]
    public void ShortLineThenNextLineBelow_NotMerged()
    {
        // 줄 끝 짧은 낱말과 다음 줄 첫 낱말이 가로로 겹쳐도 세로가 겹치지 않으면 다른 줄이다.
        var m = TextMarkupGeometry.MergeLines([R(10, 740, 30, 760), R(32, 740, 60, 760), R(10, 716, 30, 736), R(32, 716, 60, 736)]);
        Assert.Equal(2, m.Count);
    }

    [Fact]
    public void FarApartOnSameBaseline_NotMerged()
    {
        // 다단: 같은 높이여도 틈이 글자 높이의 3배를 넘으면 이어 붙이지 않는다.
        var m = TextMarkupGeometry.MergeLines([R(10, 700, 60, 716), R(300, 700, 360, 716)]);
        Assert.Equal(2, m.Count);
    }

    [Fact]
    public void VerticalWriting_MergesColumns()
    {
        // 세로쓰기: 글자가 위에서 아래로. 두 열.
        var m = TextMarkupGeometry.MergeLines([R(300, 700, 316, 716), R(300, 683, 316, 699), R(300, 666, 316, 682),
                                               R(280, 700, 296, 716), R(280, 683, 296, 699)]);
        Assert.Equal(2, m.Count);
        Assert.Equal(R(300, 666, 316, 716), m[0]);
        Assert.Equal(R(280, 683, 296, 716), m[1]);
    }

    [Fact]
    public void Diagonal_LeftUntouched()
    {
        var src = new[] { R(10, 10, 20, 20), R(22, 22, 32, 32), R(34, 34, 44, 44) };
        Assert.Equal(src, TextMarkupGeometry.MergeLines(src));
    }

    [Fact]
    public void RightToLeftOrder_StillMerges()
    {
        var m = TextMarkupGeometry.MergeLines([R(100, 700, 140, 716), R(50, 700, 90, 716), R(10, 700, 40, 716)]);
        Assert.Equal(R(10, 700, 140, 716), Assert.Single(m));
    }

    [Fact]
    public void EmptyAndSingle_Behave()
    {
        Assert.Empty(TextMarkupGeometry.MergeLines([]));
        var one = R(1, 2, 3, 4);
        Assert.Equal(one, Assert.Single(TextMarkupGeometry.MergeLines([one])));
        Assert.Empty(TextMarkupGeometry.MergeLines([R(5, 5, 5, 9)]));   // 폭 0 상자는 버린다
    }

    [Fact]
    public void ToQuads_PointOrder_MatchesFromRect()
    {
        var q = Assert.Single(TextMarkupGeometry.ToQuads([R(10, 700, 50, 716), R(60, 700, 90, 716)]));
        Assert.Equal(PdfQuadF.FromRect(R(10, 700, 90, 716)), q);
    }
}
