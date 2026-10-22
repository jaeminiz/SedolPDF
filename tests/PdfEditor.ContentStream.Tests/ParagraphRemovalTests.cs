using System.Text;

namespace PdfEditor.ContentStream.Tests;

/// <summary>M5 Task 6 / C12: 지운 원본 글을 내용 스트림에서 연산자째 제거(문자열이 파일에 남지 않게)하고, 뒤 글이 이동량에 기댈 때만 숨김으로 내려간다.</summary>
public class ParagraphRemovalTests
{
    private static (ParsedContent Pc, Segmentation Seg) Load(string s)
    {
        var pc = ContentLexer.Parse(Encoding.Latin1.GetBytes(s));
        Assert.True(pc.Ok, pc.Failure);
        var seg = ContentSegmenter.Segment(pc, null);
        Assert.True(seg.Ok, seg.Failure);
        return (pc, seg);
    }

    private static string Str(byte[] d) => Encoding.Latin1.GetString(d);

    [Fact]
    public void Removal_KeepsFollowingTextPosition()
    {
        // 다음 글이 Td로 자리를 새로 잡는다 → 연산자째 삭제해도 뒤 글 위치 불변
        var (pc, seg) = Load("BT /F1 12 Tf 50 700 Td (OLD) Tj 0 -14 Td (KEEP) Tj ET");
        var before = seg.Objects[1].Origin;
        var r = ContentRewriter.Rewrite(pc, seg, [new DisableObject(0, RemoveText: true)]);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal(1, r.DeletedTextCount);
        Assert.Equal(0, r.HiddenTextCount);
        Assert.DoesNotContain("OLD", Str(r.Data!));
        var re = ContentSegmenter.Segment(ContentLexer.Parse(r.Data!), null);
        Assert.Single(re.Objects);
        // 지운 글이 앞 Td의 위치에서 이어 쓰지 않으므로 뒤 글은 같은 자리(Td는 줄 시작 기준)
        Assert.Equal(before!.Value.X, re.Objects[0].Origin!.Value.X, 3);
        Assert.Equal(before.Value.Y, re.Objects[0].Origin!.Value.Y, 3);
    }

    [Fact]
    public void Removal_FallsBackToHide_WhenNextTextDependsOnAdvance()
    {
        var (pc, seg) = Load("BT /F1 12 Tf 50 700 Td (OLD) Tj (KEEP) Tj ET");
        var r = ContentRewriter.Rewrite(pc, seg, [new DisableObject(0, RemoveText: true)]);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal(0, r.DeletedTextCount);
        Assert.Equal(1, r.HiddenTextCount);
        Assert.Contains("3 Tr (OLD) Tj", Str(r.Data!));
        Assert.Equal(2, ContentSegmenter.Segment(ContentLexer.Parse(r.Data!), null).Objects.Count);
    }

    [Fact]
    public void Removal_ConsecutiveRemovedLines_DeletedTogether_WhenLastIsSafe()
    {
        // 두 줄을 이어 쓰고(둘 다 지움) 그 뒤 글은 Td로 재위치 → 둘 다 삭제 가능
        var (pc, seg) = Load("BT /F1 12 Tf 50 700 Td (OLD1) Tj (OLD2) Tj 0 -14 Td (KEEP) Tj ET");
        var r = ContentRewriter.Rewrite(pc, seg, [new DisableObject(0, true), new DisableObject(1, true)]);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal(2, r.DeletedTextCount);
        Assert.DoesNotContain("OLD", Str(r.Data!));
    }

    [Fact]
    public void Removal_ChainStillDependsOnKeptText_AllHidden()
    {
        // 지우는 글 뒤에 지우지 않는 글이 이어 쓴다 → 앞 글도 숨김(이동량 보존)
        var (pc, seg) = Load("BT /F1 12 Tf 50 700 Td (OLD1) Tj (OLD2) Tj (KEEP) Tj ET");
        var r = ContentRewriter.Rewrite(pc, seg, [new DisableObject(0, true), new DisableObject(1, true)]);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal(0, r.DeletedTextCount);
        Assert.Equal(2, r.HiddenTextCount);
    }

    [Fact]
    public void Removal_LastTextBeforeEt_Deleted()
    {
        var (pc, seg) = Load("BT /F1 12 Tf 50 700 Td (OLD) Tj ET BT /F1 12 Tf 60 600 Td (KEEP) Tj ET");
        var r = ContentRewriter.Rewrite(pc, seg, [new DisableObject(0, true)]);
        Assert.Equal(1, r.DeletedTextCount);
        Assert.DoesNotContain("OLD", Str(r.Data!));
    }

    [Fact]
    public void Removal_QuoteOperators_LeaveLineMove()
    {
        // ' 와 " 는 T* 를 남기고 지운다(뒤 글이 줄 이동에 기댈 수 있음)
        var (pc, seg) = Load("BT /F1 12 Tf 14 TL 50 700 Td (A) ' 1 2 (B) \" ET");
        Assert.Equal(2, seg.Objects.Count);
        var r = ContentRewriter.Rewrite(pc, seg, [new DisableObject(0, true), new DisableObject(1, true)]);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal(2, r.DeletedTextCount);
        string s = Str(r.Data!);
        Assert.DoesNotContain("(A)", s);
        Assert.DoesNotContain("(B)", s);
        Assert.Contains("T*", s);
        Assert.Contains("1 Tw 2 Tc T*", s);
    }

    [Fact]
    public void WithoutRemoveFlag_StillHides_M4xBehaviorUnchanged()
    {
        var (pc, seg) = Load("BT /F1 12 Tf 50 700 Td (OLD) Tj 0 -14 Td (KEEP) Tj ET");
        var r = ContentRewriter.Rewrite(pc, seg, [new DisableObject(0)]);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal(0, r.DeletedTextCount);
        Assert.Contains("3 Tr (OLD) Tj", Str(r.Data!));
    }
}
