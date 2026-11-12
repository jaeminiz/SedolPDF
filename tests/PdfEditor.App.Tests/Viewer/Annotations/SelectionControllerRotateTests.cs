using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Annotations;

/// <summary>0.10.0 Task 7: 주석 회전 손잡이(초록 손잡이) 끌기.</summary>
public class SelectionControllerRotateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly PageId Page = new(7);
    private static readonly PdfRectF Bounds = new(0, 0, 600, 800);
    private const float Deg = MathF.PI / 180f;

    private static Annotation Line(long id, float x0, float y0, float x1, float y1)
    {
        var a = new Annotation { Id = new AnnotId(id), Kind = AnnotKind.Line, BorderWidth = 1f, Vertices = [new(x0, y0), new(x1, y1)] };
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private static Annotation Square(long id, float l, float b, float r, float t)
        => new() { Id = new AnnotId(id), Kind = AnnotKind.Square, Rect = new PdfRectF(l, b, r, t), BorderWidth = 1f };

    private static Annotation Highlight(long id, float l, float b, float r, float t)
    {
        var a = new Annotation { Id = new AnnotId(id), Kind = AnnotKind.Highlight, Quads = [PdfQuadF.FromRect(new PdfRectF(l, b, r, t))] };
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private static PageAnnotations Snap(params Annotation[] items) => new(Page, [.. items.Select(a => new AnnotationView(a, true))]);

    private static AnnotationSelection Sel(params long[] ids)
    {
        var s = new AnnotationSelection();
        s.Set(Page, [.. ids.Select(i => new AnnotId(i))]);
        return s;
    }

    private static SelectionController Make() => new(() => Now);

    private static AnnotationChange[] Changes(ChangeAnnotationsCommand cmd)
        => (AnnotationChange[])typeof(ChangeAnnotationsCommand)
            .GetField("_changes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cmd)!;

    [Fact]
    public void HitTest_FindsRotateHandle_AboveSelection()
    {
        var page = Snap(Line(1, 100, 100, 200, 100), Line(2, 100, 140, 200, 140));
        var sel = Sel(1, 2);
        var b = SelectionController.RotatableBounds(page, sel)!.Value;
        var hp = ObjectSelectionController.RotateHandlePos(b, 0, 1f);
        var hit = Make().HitTest(page, sel, hp, 1f);
        Assert.NotNull(hit);
        Assert.Equal(HandleKind.Rotate, hit!.Handle);
        Assert.Equal(new((b.Left + b.Right) / 2, b.Top + 24), hp);   // 위쪽 가운데에서 24 DIP 위
    }

    [Fact]
    public void MixedWithHighlight_NoRotateHandle()
    {
        var page = Snap(Line(1, 100, 100, 200, 100), Highlight(2, 100, 140, 200, 150));
        var sel = Sel(1, 2);
        Assert.Null(SelectionController.RotatableBounds(page, sel));
        Assert.Null(SelectionController.RotatableBounds(page, Sel()));
        var b = page.Items.Select(v => v.Data.Rect).Aggregate((x, y) => x.Union(y));
        var hit = Make().HitTest(page, sel, ObjectSelectionController.RotateHandlePos(b, 0, 1f), 1f);
        Assert.True(hit is null || hit.Handle != HandleKind.Rotate);
    }

    [Fact]
    public void Drag_Shift_Snaps15()
    {
        var page = Snap(Line(1, 100, 100, 200, 100));
        var sel = Sel(1);
        var c = Make();
        var b = SelectionController.RotatableBounds(page, sel)!.Value;
        var center = new PdfPointF((b.Left + b.Right) / 2, (b.Bottom + b.Top) / 2);
        var start = new PdfPointF(center.X, center.Y + 100);
        c.Begin(page, sel, new HitResult(new AnnotId(1), HandleKind.Rotate), start, Bounds, 1f);
        Assert.True(c.IsRotating);
        // 시작에서 반시계로 37°
        float a = 90 * Deg + 37 * Deg;
        c.Move(new(center.X + 100 * MathF.Cos(a), center.Y + 100 * MathF.Sin(a)), shift: false);
        Assert.Equal(37 * Deg, c.RotationPreview, 0.01f);
        c.Move(new(center.X + 100 * MathF.Cos(a), center.Y + 100 * MathF.Sin(a)), shift: true);
        Assert.Equal(30 * Deg, c.RotationPreview, 0.01f);
        Assert.Equal(90 * Deg, c.RotationStartAngle, 0.01f);
    }

    [Fact]
    public void End_ReturnsOneCommand_UndoRestores()
    {
        var l1 = Line(1, 100, 100, 200, 100);
        var l2 = Line(2, 100, 140, 200, 140);
        var sq = Square(3, 100, 180, 160, 200);
        var page = Snap(l1, l2, sq);
        var sel = Sel(1, 2, 3);
        var c = Make();
        var b = SelectionController.RotatableBounds(page, sel)!.Value;
        var center = new PdfPointF((b.Left + b.Right) / 2, (b.Bottom + b.Top) / 2);
        c.Begin(page, sel, new HitResult(new AnnotId(1), HandleKind.Rotate), new(center.X, center.Y + 100), Bounds, 1f);
        float a = 90 * Deg + 37 * Deg;
        var cmd = c.End(new(center.X + 100 * MathF.Cos(a), center.Y + 100 * MathF.Sin(a)));
        Assert.NotNull(cmd);
        var changes = Changes(cmd!);
        Assert.Equal(2, changes.Length);   // 37°는 사각형을 0°로 맞추므로 바뀌는 것은 두 선뿐
        var after = changes.ToDictionary(x => x.After!.Id, x => x.After!);
        Assert.NotEqual(l1.Vertices[1], after[new AnnotId(1)].Vertices[1]);   // 선은 돌아감
        Assert.NotEqual(l2.Vertices[1], after[new AnnotId(2)].Vertices[1]);
        Assert.DoesNotContain(new AnnotId(3), after.Keys);
        Assert.All(after.Values, x => Assert.Equal(Now, x.ModifiedAt));
        Assert.Null(c.Previews);
    }

    [Fact]
    public void End_SmallAngle_OnlyQuarterKinds_NoCommand()
    {
        var page = Snap(Square(3, 100, 180, 160, 200));
        var sel = Sel(3);
        var c = Make();
        var b = SelectionController.RotatableBounds(page, sel)!.Value;
        var center = new PdfPointF((b.Left + b.Right) / 2, (b.Bottom + b.Top) / 2);
        c.Begin(page, sel, new HitResult(new AnnotId(3), HandleKind.Rotate), new(center.X, center.Y + 100), Bounds, 1f);
        float a = 90 * Deg + 20 * Deg;
        Assert.Null(c.End(new(center.X + 100 * MathF.Cos(a), center.Y + 100 * MathF.Sin(a))));
    }

    [Fact]
    public void Preview_FreeKindsUseRawAngle_QuarterKindsStayPut()
    {
        var l = Line(1, 100, 100, 200, 100);
        var sq = Square(2, 100, 140, 160, 160);
        var page = Snap(l, sq);
        var sel = Sel(1, 2);
        var c = Make();
        var b = SelectionController.RotatableBounds(page, sel)!.Value;
        var center = new PdfPointF((b.Left + b.Right) / 2, (b.Bottom + b.Top) / 2);
        c.Begin(page, sel, new HitResult(new AnnotId(1), HandleKind.Rotate), new(center.X, center.Y + 100), Bounds, 1f);
        float a = 90 * Deg + 37 * Deg;
        c.Move(new(center.X + 100 * MathF.Cos(a), center.Y + 100 * MathF.Sin(a)), false);
        Assert.NotEqual(l.Vertices[1], c.Previews![l.Id]!.Vertices[1]);
        Assert.Equal(sq.Rect, c.Previews![sq.Id]!.Rect);
    }

    [Fact]
    public void RotatedPage_HandleAboveOnScreen_AndClockwiseDragIsClockwise()
    {
        const int rot = 1;   // 쪽이 시계 방향으로 90° 돌아 보임
        var l = Line(1, 100, 100, 300, 100);
        var page = Snap(l);
        var sel = Sel(1);
        var c = Make();
        var b = SelectionController.RotatableBounds(page, sel)!.Value;
        float ptPerDip = 1f;
        var hp = ObjectSelectionController.RotateHandlePos(b, rot, ptPerDip);
        var tr = new PageTransform(new System.Drawing.RectangleF(Bounds.Left, Bounds.Bottom, Bounds.Width, Bounds.Height), rot, 1);
        var center = new PdfPointF((b.Left + b.Right) / 2, (b.Bottom + b.Top) / 2);

        // 화면에서 손잡이는 경계 중심의 위쪽
        var vc = tr.ToView(center.X, center.Y); var vh = tr.ToView(hp.X, hp.Y);
        Assert.True(vh.Y < vc.Y - 1 && Math.Abs(vh.X - vc.X) < 1e-6);
        Assert.Equal(HandleKind.Rotate, c.HitTest(page, sel, hp, ptPerDip, rot)!.Handle);
        Assert.NotEqual(HandleKind.Rotate, c.HitTest(page, sel, ObjectSelectionController.RotateHandlePos(b, 0, ptPerDip), ptPerDip, rot)?.Handle ?? HandleKind.None);

        // 화면에서 시계 방향 90°로 끈다: 위 → 오른쪽(화면 y는 아래로 늘어난다)
        double rx = vh.X - vc.X, ry = vh.Y - vc.Y;
        var target = tr.ToPage(new ViewPoint(vc.X + (-ry), vc.Y + rx));
        c.Begin(page, sel, new HitResult(l.Id, HandleKind.Rotate), hp, Bounds, ptPerDip);
        c.Move(new PdfPointF((float)target.X, (float)target.Y), false);
        var rotated = c.Previews![l.Id]!;

        // 화면에서 선의 끝점이 중심을 기준으로 시계 방향으로 돌았는가(외적 부호: 화면 y가 아래이므로 시계 = +)
        var v0 = tr.ToView(l.Vertices[1].X, l.Vertices[1].Y);
        var v1 = tr.ToView(rotated.Vertices[1].X, rotated.Vertices[1].Y);
        double cross = (v0.X - vc.X) * (v1.Y - vc.Y) - (v0.Y - vc.Y) * (v1.X - vc.X);
        Assert.True(cross > 0, $"cross {cross}");
        Assert.Equal(90 * Deg, -c.RotationPreview, 0.02f);   // 쪽 좌표에서는 시계 = 음수
    }

    [Fact]
    public void PreviewLabel_ShowsSnappedAngle_WhenQuarterKindsSelected()
    {
        Assert.Equal("37.4° → 0°", SelectionController.RotationLabel(37.4f * Deg, true));
        Assert.Equal("37.4°", SelectionController.RotationLabel(37.4f * Deg, false));
        Assert.Equal("350.0° → 0°", SelectionController.RotationLabel(-10f * Deg, true));
        Assert.Equal("80.0° → 90°", SelectionController.RotationLabel(80f * Deg, true));
    }

    [Fact]
    public void OldMoveOverload_StillMovesBody()
    {
        var sq = Square(1, 100, 100, 200, 200);
        var page = Snap(sq);
        var c = Make();
        c.Begin(page, Sel(), new HitResult(sq.Id, HandleKind.Body), new(150, 150), Bounds, 1f);
        c.Move(new PdfPointF(160, 150));
        Assert.Equal(110f, c.Previews![sq.Id]!.Rect.Left, 0.01f);
    }
}
