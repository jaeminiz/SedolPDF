using Pdf.Native;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Objects;

public class ObjectSelectionControllerTests
{
    private static readonly PageId Page = new(1);

    private static PageObjectIndex Index(params PageObjectEntry[] e) => new(Page, 0, e);

    private static PageObjectEntry E(int i, PdfPageObjectType t, PdfRectF b, bool frame = false) =>
        new(ObjectRef.Original(i), t, i, b, PdfQuadF.FromRect(b), null, frame);

    [Fact]
    public void Pick_TopmostSkippingFrame_AltCyclesDown()
    {
        var frame = E(0, PdfPageObjectType.Path, new PdfRectF(0, 0, 600, 800), frame: true);
        var low = E(1, PdfPageObjectType.Image, new PdfRectF(100, 100, 300, 300));
        var high = E(2, PdfPageObjectType.Path, new PdfRectF(150, 150, 250, 250));
        var idx = Index(frame, low, high);
        var c = new ObjectSelectionController();
        var p = new PdfPointF(200, 200);
        Assert.Equal(high.Ref, c.Pick(idx, p, 1, cycle: false)!.Ref);
        c.Select(new SelectedObject(Page, high.Ref, high.Type, high.Bounds));
        Assert.Equal(low.Ref, c.Pick(idx, p, 1, cycle: true)!.Ref);
        c.Select(new SelectedObject(Page, low.Ref, low.Type, low.Bounds));
        Assert.Equal(frame.Ref, c.Pick(idx, p, 1, cycle: true)!.Ref);   // 테두리는 맨 뒤
        Assert.Equal(frame.Ref, c.Pick(idx, new PdfPointF(500, 700), 1, cycle: false)!.Ref);   // 다른 후보가 없으면 테두리
    }

    [Fact]
    public void Rotate_HandleAboveTop_Drag90Degrees_RotatesAboutCenter()
    {
        var c = new ObjectSelectionController();
        var b = new PdfRectF(100, 100, 300, 200);   // 중심 (200, 150)
        c.Select(new SelectedObject(Page, ObjectRef.Original(1), PdfPageObjectType.Path, b));
        var hp = ObjectSelectionController.RotateHandlePos(b, 0, 1f);
        Assert.Equal(200f, hp.X, 3); Assert.True(hp.Y > 200f);
        Assert.Equal(ObjectHandle.Rotate, c.HitHandle(hp, 1f));
        c.BeginDrag(ObjectHandle.Rotate, hp);
        c.Drag(new PdfPointF(100, 150), shift: false);   // 위쪽에서 왼쪽으로 = 반시계 90도
        var m = c.EndDrag()!.Value;
        var q = m.Apply(new PdfPointF(300, 150));   // 오른쪽 끝 → 위쪽 끝
        Assert.Equal(200f, q.X, 2); Assert.Equal(250f, q.Y, 2);
        var center = m.Apply(new PdfPointF(200, 150));
        Assert.Equal(200f, center.X, 2); Assert.Equal(150f, center.Y, 2);
    }

    [Fact]
    public void Rotate_NoMovement_ReturnsNull_ShiftSnaps15()
    {
        var c = new ObjectSelectionController();
        var b = new PdfRectF(100, 100, 300, 200);
        c.Select(new SelectedObject(Page, ObjectRef.Original(1), PdfPageObjectType.Path, b));
        var hp = ObjectSelectionController.RotateHandlePos(b, 0, 1f);
        c.BeginDrag(ObjectHandle.Rotate, hp);
        Assert.Null(c.EndDrag());
        c.BeginDrag(ObjectHandle.Rotate, hp);
        c.Drag(new PdfPointF(200 + 100 * (float)Math.Sin(-0.3), 150 + 100 * (float)Math.Cos(-0.3)), shift: true);   // 왼쪽으로 약 17도(반시계 +) → 15도
        Assert.Equal(Math.PI / 12, c.RotationPreview, 3);
        c.CancelDrag();
        Assert.Equal(0f, c.RotationPreview);
    }

    [Theory]
    [InlineData(1, 90f, 150f)]
    [InlineData(2, 200f, 90f)]
    [InlineData(3, 310f, 150f)]
    public void RotateHandle_FollowsScreenUpOnRotatedPages(int rot, float x, float y)
    {
        var b = new PdfRectF(100, 100, 300, 200);
        var hp = ObjectSelectionController.RotateHandlePos(b, rot, 10f / 24f);   // 거리 10pt
        Assert.Equal(x, hp.X, 2); Assert.Equal(y, hp.Y, 2);
    }

    [Fact]
    public void Drag_Body_Moves_ResizeKeepsAspectForImages_ShiftFree()
    {
        var c = new ObjectSelectionController();
        var b = new PdfRectF(100, 100, 300, 200);   // 200×100
        c.Select(new SelectedObject(Page, ObjectRef.Original(1), PdfPageObjectType.Image, b));
        c.BeginDrag(ObjectHandle.Body, new PdfPointF(150, 150));
        c.Drag(new PdfPointF(170, 120), shift: false);
        var m = c.EndDrag()!.Value;
        Assert.Equal(new PdfPointF(120, 70), m.Apply(new PdfPointF(100, 100)));

        c.BeginDrag(ObjectHandle.TopRight, new PdfPointF(300, 200));
        c.Drag(new PdfPointF(500, 220), shift: false);   // 가로 2배, 세로 1.2배 → 비율 고정이면 2배
        Assert.Equal(400, c.Preview!.Value.Width, 0.01f);
        Assert.Equal(200, c.Preview!.Value.Height, 0.01f);
        Assert.Equal(100, c.Preview!.Value.Left, 0.01f);   // 반대 모서리(왼쪽 아래) 고정
        c.Drag(new PdfPointF(500, 220), shift: true);    // Shift: 자유
        Assert.Equal(120, c.Preview!.Value.Height, 0.01f);
        var r = c.EndDrag()!.Value;
        Assert.Equal(new PdfPointF(500, 220), r.Apply(new PdfPointF(300, 200)));
    }

    [Fact]
    public void Drag_NoMovement_NoEdit_HandlesHitByCorner()
    {
        var c = new ObjectSelectionController();
        c.Select(new SelectedObject(Page, ObjectRef.Original(1), PdfPageObjectType.Path, new PdfRectF(100, 100, 300, 200)));
        Assert.Equal(ObjectHandle.BottomLeft, c.HitHandle(new PdfPointF(101, 101), 0.75f));
        Assert.Equal(ObjectHandle.Body, c.HitHandle(new PdfPointF(200, 150), 0.75f));
        Assert.Equal(ObjectHandle.None, c.HitHandle(new PdfPointF(50, 50), 0.75f));
        c.BeginDrag(ObjectHandle.Body, new PdfPointF(200, 150));
        Assert.Null(c.EndDrag());
    }

    [Fact]
    public void Refresh_FollowsBounds_ClearsWhenGone()
    {
        var c = new ObjectSelectionController();
        c.Select(new SelectedObject(Page, ObjectRef.Original(1), PdfPageObjectType.Path, new PdfRectF(0, 0, 10, 10)));
        c.Refresh(Index(E(1, PdfPageObjectType.Path, new PdfRectF(5, 5, 15, 15))));
        Assert.Equal(5, c.Selected!.Bounds.Left);
        c.Refresh(Index());
        Assert.Null(c.Selected);
    }

    [Fact]
    public void Refresh_ManySelectedOnHugePage_StaysFast_AndKeepsOnlySurvivors()
    {
        // 큰 도면(20만 객체)에서 사각형 끌기로 수천 개를 고른 뒤 편집 때마다 선택을 따라간다(UI 스레드) — 선택 수 x 객체 수로 훑으면 수 초.
        var entries = Enumerable.Range(0, 200_000).Select(i => E(i, PdfPageObjectType.Path, new PdfRectF(i % 500, i / 500, i % 500 + 1, i / 500 + 1))).ToArray();
        var c = new ObjectSelectionController();
        c.SelectMany(Enumerable.Range(0, 5000).Select(i => new SelectedObject(Page, ObjectRef.Original(i * 3), PdfPageObjectType.Path, new PdfRectF(0, 0, 1, 1))));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        c.Refresh(new PageObjectIndex(Page, 1, entries.Where(e => e.Ref.OriginalIndex != 3).ToArray()));   // 하나는 사라짐
        sw.Stop();
        Assert.Equal(4999, c.All.Count);
        Assert.True(sw.ElapsedMilliseconds < 500, $"Refresh took {sw.ElapsedMilliseconds} ms");
    }
}
