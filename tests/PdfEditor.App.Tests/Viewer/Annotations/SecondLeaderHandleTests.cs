using Pdf.Native;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Annotations;

/// <summary>0.4.0 Task 3: 둘째 지시선의 선택 손잡이·끌기·상자 이동.</summary>
public class SecondLeaderHandleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    private static readonly PageId Page = new(7);
    private static readonly PdfRectF Bounds = new(0, 0, 600, 800);

    private static Annotation Callout2()
    {
        var box = new PdfRectF(200, 260, 360, 300);
        var a = new Annotation
        {
            Id = new AnnotId(1), Kind = AnnotKind.FreeText, Intent = FreeTextIntent.Callout, TextBox = box, BorderWidth = 1f,
            Vertices = [new PdfPointF(100, 280), new PdfPointF(200, 280)], EndEnding = LineEnding.OpenArrow, Contents = "x",
        };
        return CalloutLeaders.AddSecond(a with { Rect = AnnotationGeometry.ComputeRect(a) });
    }

    private static PageAnnotations Snap(Annotation a) => new(Page, [new AnnotationView(a, true)]);

    private static AnnotationSelection Sel()
    {
        var s = new AnnotationSelection();
        s.Set(Page, [new AnnotId(1)]);
        return s;
    }

    private static AnnotationChange[] Changes(ChangeAnnotationsCommand cmd)
        => (AnnotationChange[])typeof(ChangeAnnotationsCommand)
            .GetField("_changes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cmd)!;

    [Fact]
    public void SecondLeaderTip_HasHandle_DragMovesOnlyThatTip()
    {
        var co = Callout2();
        Assert.Equal(1 + 1 + 8, SelectionController.HandlesOf(co).Count);   // 첫 끝점 + 둘째 끝점 + 상자 8개
        var c = new SelectionController(() => Now);
        var tip2 = co.Leader2[0];
        var hit = c.HitTest(Snap(co), Sel(), tip2, 1f);
        Assert.Equal(HandleKind.LeaderTip, hit!.Handle);
        Assert.Equal(1, hit.Index);

        c.Begin(Snap(co), Sel(), hit, tip2, Bounds, 1f);
        var cmd = c.End(new PdfPointF(tip2.X + 30, tip2.Y + 20));
        Assert.NotNull(cmd);
        var after = Changes(cmd!).Single().After!;
        Assert.Equal(tip2.X + 30, after.Leader2[0].X, 0.01);
        Assert.Equal(tip2.Y + 20, after.Leader2[0].Y, 0.01);
        Assert.Equal(co.Vertices[0], after.Vertices[0]);       // 첫 지시선은 그대로
        Assert.Equal(co.TextBox, after.TextBox);
        Assert.Equal(AnnotationGeometry.ComputeRect(after), after.Rect);
        Assert.Equal(3, after.Leader2.Length);                 // 점 개수 그대로
    }

    [Fact]
    public void FirstLeaderTip_StillMovesFirstLeaderOnly()
    {
        var co = Callout2();
        var c = new SelectionController(() => Now);
        var hit = c.HitTest(Snap(co), Sel(), co.Vertices[0], 1f);
        Assert.Equal(HandleKind.LeaderTip, hit!.Handle);
        Assert.Equal(0, hit.Index);
        c.Begin(Snap(co), Sel(), hit, co.Vertices[0], Bounds, 1f);
        var after = Changes(c.End(new PdfPointF(80, 250))!).Single().After!;
        Assert.Equal(new PdfPointF(80, 250), after.Vertices[0]);
        Assert.Equal(co.Leader2[0], after.Leader2[0]);
    }

    [Fact]
    public void BoxMove_TranslatesBothLeaders()
    {
        var co = Callout2();
        var c = new SelectionController(() => Now);
        c.Begin(Snap(co), Sel(), new HitResult(co.Id, HandleKind.Body), new PdfPointF(280, 280), Bounds, 1f);
        var after = Changes(c.End(new PdfPointF(300, 270))!).Single().After!;
        Assert.Equal(co.Leader2[0].X + 20, after.Leader2[0].X, 0.01);
        Assert.Equal(co.Leader2[0].Y - 10, after.Leader2[0].Y, 0.01);
        Assert.Equal(co.Vertices[0].X + 20, after.Vertices[0].X, 0.01);
    }

    [Fact]
    public void BoxResize_ReanchorsSecondLeaderToNearestEdge()
    {
        var co = Callout2();   // 둘째 지시선은 오른쪽 변(x=360)에 닿는다
        var c = new SelectionController(() => Now);
        c.Begin(Snap(co), Sel(), new HitResult(co.Id, HandleKind.N), new PdfPointF(280, 300), Bounds, 1f);
        var after = Changes(c.End(new PdfPointF(280, 340))!).Single().After!;
        Assert.Equal(360f, after.Leader2[^1].X, 0.01);                 // 같은 변
        Assert.Equal((260f + 340f) / 2f, after.Leader2[^1].Y, 0.01);    // 새 높이의 가운데
    }
}
