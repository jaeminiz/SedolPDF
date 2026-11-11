using System.Text;

namespace PdfEditor.ContentStream.Tests;

/// <summary>0.9.0 Task 3: 객체 하나를 혼자 서는 조각으로 꺼내기, 자원 이름 바꾸기.</summary>
public class ObjectSnippetTests
{
    private static (ParsedContent Pc, Segmentation Seg) Load(string s, Func<string, XObjectInfo?>? x = null)
    {
        var pc = ContentLexer.Parse(Encoding.Latin1.GetBytes(s));
        Assert.True(pc.Ok, pc.Failure);
        var seg = ContentSegmenter.Segment(pc, x);
        Assert.True(seg.Ok, seg.Failure);
        return (pc, seg);
    }

    private static XObjectInfo? ImageOnly(string n) => n == "Im1" ? new XObjectInfo(ObjectKind.Image, null, null) : null;

    [Fact]
    public void Segment_PathAndImage_HaveReplayOps()
    {
        var (pc, seg) = Load("0.5 w 1 0 0 rg /GS1 gs 0 0 m 10 10 l S q 10 0 0 10 0 0 cm /Im1 Do Q", ImageOnly);
        var names = (PageObject o) => o.ReplayOps.Select(i => pc.Ops[i].Name).ToList();
        Assert.Equal(2, seg.Objects.Count);
        Assert.Contains("w", names(seg.Objects[0]));
        Assert.Contains("rg", names(seg.Objects[0]));
        Assert.Contains("gs", names(seg.Objects[0]));
        Assert.Contains("gs", names(seg.Objects[1]));   // 그림은 앞에서 정한 상태를 물려받는다
    }

    [Fact]
    public void Extract_Path_ReparsesAndSegmentsToOneObject()
    {
        var (pc, seg) = Load("q 2 0 0 2 10 20 cm 1 0 0 rg 0 0 m 50 0 l 50 30 l f Q");
        var r = ObjectSnippet.Extract(pc, seg, 0, Mat.Translate(100, -5));
        Assert.True(r.Ok, r.Failure);
        var re = ContentLexer.Parse(Encoding.Latin1.GetBytes(r.Content!));
        Assert.True(re.Ok, re.Failure);
        var seg2 = ContentSegmenter.Segment(re);
        var o = Assert.Single(seg2.Objects);
        Assert.Equal(ObjectKind.Path, o.Kind);
        var orig = seg.Objects[0].Bounds!.Value;
        var b = o.Bounds!.Value;
        Assert.Equal(orig.L + 100, b.L, 6); Assert.Equal(orig.R + 100, b.R, 6);
        Assert.Equal(orig.B - 5, b.B, 6); Assert.Equal(orig.T - 5, b.T, 6);
    }

    [Fact]
    public void Extract_Text_KeepsFontAndState()
    {
        var (pc, seg) = Load("BT /F1 12 Tf 2 Tc 100 700 Td (Hi) Tj ET");
        var r = ObjectSnippet.Extract(pc, seg, 0, Mat.Identity);
        Assert.True(r.Ok, r.Failure);
        Assert.Contains("/F1 12 Tf", r.Content);
        Assert.Contains("2 Tc", r.Content);
        Assert.Contains("BT", r.Content);
        Assert.Contains("ET", r.Content);
        Assert.Equal([new ResourceUse("Font", "F1")], r.Uses);
        var re = ContentSegmenter.Segment(ContentLexer.Parse(Encoding.Latin1.GetBytes(r.Content!)));
        Assert.Single(re.Objects);
    }

    [Fact]
    public void Extract_TextColorOverride_WritesRgAfterState()
    {
        var (pc, seg) = Load("0 g BT /F1 12 Tf 100 700 Td (Hi) Tj ET");
        var r = ObjectSnippet.Extract(pc, seg, 0, Mat.Identity, (1, 0, 0));
        Assert.True(r.Ok, r.Failure);
        int g = r.Content!.IndexOf("0 g", StringComparison.Ordinal);
        int rg = r.Content.IndexOf("1 0 0 rg", StringComparison.Ordinal);
        Assert.True(g >= 0 && rg > g, r.Content);
    }

    [Fact]
    public void Extract_ImageDo_UsesXObject()
    {
        var (pc, seg) = Load("/GS1 gs q 10 0 0 10 0 0 cm /Im1 Do Q", ImageOnly);
        var r = ObjectSnippet.Extract(pc, seg, 0, Mat.Translate(5, 5));
        Assert.True(r.Ok, r.Failure);
        Assert.Contains(new ResourceUse("XObject", "Im1"), r.Uses);
        Assert.Contains(new ResourceUse("ExtGState", "GS1"), r.Uses);
        Assert.Contains("/Im1 Do", r.Content);
    }

    [Fact]
    public void Extract_PathWithClip_Fails()
    {
        var (pc, seg) = Load("0 0 10 10 re W f");
        var r = ObjectSnippet.Extract(pc, seg, 0, Mat.Identity);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Rename_ClashingName_GetsSedolR1()
    {
        var (pc, seg) = Load("BT /F1 12 Tf 100 700 Td (F1 inside) Tj ET");
        var r = ObjectSnippet.Extract(pc, seg, 0, Mat.Identity);
        string renamed = ObjectSnippet.Rename(r.Content!, new Dictionary<ResourceUse, string> { [new ResourceUse("Font", "F1")] = "SedolR1" });
        Assert.Contains("/SedolR1 12 Tf", renamed);
        Assert.Contains("(F1 inside)", renamed);
        Assert.DoesNotContain("/F1 12 Tf", renamed);
    }
}
