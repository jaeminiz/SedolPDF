using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Annotations;

public class SelectionControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly PageId Page = new(7);
    private static readonly PdfRectF Bounds = new(0, 0, 600, 800);

    private static Annotation Square(long id, float l, float b, float r, float t) => new()
    {
        Id = new AnnotId(id), Kind = AnnotKind.Square, Rect = new PdfRectF(l, b, r, t), BorderWidth = 1f,
        Color = PdfColor.FromHex("#E5372D"),
    };

    private static Annotation Ink(long id, params (float, float)[] pts) => new()
    {
        Id = new AnnotId(id), Kind = AnnotKind.Ink, BorderWidth = 2f,
        InkStrokes = [[.. pts.Select(p => new PdfPointF(p.Item1, p.Item2))]],
    };

    private static Annotation Note(long id, float l, float b) => new()
    {
        Id = new AnnotId(id), Kind = AnnotKind.Text, Rect = new PdfRectF(l, b, l + 20, b + 20),
    };

    private static Annotation Highlight(long id, float l, float b, float r, float t)
    {
        var a = new Annotation { Id = new AnnotId(id), Kind = AnnotKind.Highlight, Quads = [PdfQuadF.FromRect(new PdfRectF(l, b, r, t))] };
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private static PageAnnotations Snap(params Annotation[] items)
        => new(Page, [.. items.Select(a => new AnnotationView(a, true))]);

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
    public void HitTest_TopmostWins()
    {
        var low = Square(1, 0, 0, 100, 100) with { FillColor = PdfColor.FromHex("#FFFF00") };
        var top = Square(2, 50, 50, 150, 150) with { FillColor = PdfColor.FromHex("#00FF00") };
        var hit = Make().HitTest(Snap(low, top), Sel(), new(75, 75), 1f);
        Assert.Equal(new HitResult(new AnnotId(2), HandleKind.Body), hit);
        Assert.Null(Make().HitTest(Snap(low, top), Sel(), new(400, 400), 1f));
    }

    [Fact]
    public void HitTest_HandleBeatsBodyForSingleResizable()
    {
        var sq = Square(1, 100, 100, 200, 200) with { FillColor = PdfColor.FromHex("#FFFF00") };
        var c = Make();
        Assert.Equal(new HitResult(sq.Id, HandleKind.SE), c.HitTest(Snap(sq), Sel(1), new(202, 98), 1f));
        Assert.Equal(new HitResult(sq.Id, HandleKind.N), c.HitTest(Snap(sq), Sel(1), new(150, 201), 1f));
        // handle size scales with ptPerDip: 3 pt away is outside at 1 pt/DIP, inside at 2 pt/DIP
        Assert.Equal(HandleKind.Body, c.HitTest(Snap(sq), Sel(1), new(204.5f, 95.5f), 1f)?.Handle ?? HandleKind.Body);
        Assert.Equal(HandleKind.SE, c.HitTest(Snap(sq), Sel(1), new(206f, 94f), 2f)!.Handle);
        // not selected: body, no handles
        Assert.Equal(HandleKind.Body, c.HitTest(Snap(sq), Sel(), new(200, 100), 1f)!.Handle);
        // two selected: no handles
        var sq2 = Square(2, 300, 300, 350, 350);
        Assert.Equal(HandleKind.Body, c.HitTest(Snap(sq, sq2), Sel(1, 2), new(200, 100), 1f)!.Handle);
    }

    [Fact]
    public void HitTest_NoHandlesForTextOrMarkup()
    {
        var note = Note(1, 100, 100);
        var hl = Highlight(2, 300, 300, 400, 320);
        var c = Make();
        Assert.Equal(HandleKind.Body, c.HitTest(Snap(note), Sel(1), new(120, 100), 1f)!.Handle);
        Assert.Equal(HandleKind.Body, c.HitTest(Snap(hl), Sel(2), new(400, 300), 1f)!.Handle);
    }

    [Fact]
    public void Move_TwoSelected_OneCommandSameDelta()
    {
        var a = Square(1, 100, 100, 150, 150);
        var b = Ink(2, (300, 300), (340, 320));
        var snap = Snap(a, b);
        var sel = Sel(1, 2);
        var c = Make();
        c.Begin(snap, sel, new HitResult(a.Id, HandleKind.Body), new(120, 120), Bounds, 1f);
        c.Move(new(130, 115));
        Assert.Equal(2, c.Previews!.Count);
        var cmd = c.End(new(130, 115));
        var ch = Changes(cmd!);
        Assert.Equal(2, ch.Length);
        Assert.Same(a, ch[0].Before);
        Assert.Same(b, ch[1].Before);
        Assert.Equal(new PdfRectF(110, 95, 160, 145), ch[0].After!.Rect);
        Assert.Equal(new PdfPointF(310, 295), ch[1].After!.InkStrokes[0][0]);
        Assert.Equal(Now, ch[0].After!.ModifiedAt);
        Assert.Equal(Now, ch[1].After!.ModifiedAt);
        Assert.Null(c.Previews);
    }

    [Fact]
    public void Move_ClampedInsidePage()
    {
        var a = Square(1, 10, 10, 60, 60);
        var b = Square(2, 500, 700, 550, 750);
        var c = Make();
        c.Begin(Snap(a, b), Sel(1, 2), new HitResult(a.Id, HandleKind.Body), new(30, 30), Bounds, 1f);
        var cmd = c.End(new(-500, 900));
        var ch = Changes(cmd!);
        // union is (10,10)-(550,750): dx limited to -10, dy limited to +50
        Assert.Equal(new PdfRectF(0, 60, 50, 110), ch[0].After!.Rect);
        Assert.Equal(new PdfRectF(490, 750, 540, 800), ch[1].After!.Rect);
    }

    [Fact]
    public void Move_BelowOneDip_NoCommand()
    {
        var a = Square(1, 100, 100, 150, 150);
        var c = Make();
        c.Begin(Snap(a), Sel(1), new HitResult(a.Id, HandleKind.Body), new(120, 120), Bounds, 1f);
        Assert.Null(c.End(new(120.5f, 120.5f)));
        // at 2 pt/DIP the threshold is 2 pt
        c.Begin(Snap(a), Sel(1), new HitResult(a.Id, HandleKind.Body), new(120, 120), Bounds, 2f);
        Assert.Null(c.End(new(121.5f, 120)));
    }

    [Fact]
    public void Begin_BodyOnUnselected_SelectsIt()
    {
        var a = Square(1, 100, 100, 150, 150);
        var sel = Sel();
        var c = Make();
        c.Begin(Snap(a), sel, new HitResult(a.Id, HandleKind.Body), new(120, 120), Bounds, 1f);
        Assert.Equal([a.Id], sel.Ids);
    }

    [Fact]
    public void Resize_SE_InkPointsScaled()
    {
        var ink = Ink(1, (100, 100), (200, 150));
        var rect = AnnotationGeometry.ComputeRect(ink); // inflated by border/2 + 1 = 2
        var c = Make();
        c.Begin(Snap(ink), Sel(1), new HitResult(ink.Id, HandleKind.SE), new(rect.Right, rect.Bottom), Bounds, 1f);
        // dragging SE: Left/Top fixed, Right +100, Bottom -50
        var cmd = c.End(new(rect.Right + 100, rect.Bottom - 50));
        var after = Changes(cmd!)[0].After!;
        var pts = after.InkStrokes[0];
        Assert.Equal(100f, pts[0].X, 3);
        Assert.Equal(50f, pts[0].Y, 3);
        Assert.Equal(300f, pts[1].X, 3);
        Assert.Equal(150f, pts[1].Y, 3);
        Assert.Equal(rect.Left, after.Rect.Left, 3);
        Assert.Equal(rect.Top, after.Rect.Top, 3);
        Assert.Equal(rect.Right + 100, after.Rect.Right, 3);
        Assert.Equal(rect.Bottom - 50, after.Rect.Bottom, 3);
        Assert.Equal(Now, after.ModifiedAt);
    }

    [Fact]
    public void Resize_NW_KeepsOppositeCornerFixed()
    {
        var sq = Square(1, 100, 100, 200, 200);
        var c = Make();
        c.Begin(Snap(sq), Sel(1), new HitResult(sq.Id, HandleKind.NW), new(100, 200), Bounds, 1f);
        var after = Changes(c.End(new(80, 230))!)[0].After!;
        Assert.Equal(new PdfRectF(80, 100, 200, 230), after.Rect);
    }

    [Fact]
    public void Resize_GrabOffCentre_NoJump_AndPlainClickMakesNoCommand()
    {
        var sq = Square(1, 100, 100, 200, 200);
        var c = Make();
        c.Begin(Snap(sq), Sel(1), new HitResult(sq.Id, HandleKind.SE), new(203, 97), Bounds, 1f);
        Assert.Null(c.End(new(203, 97)));
        c.Begin(Snap(sq), Sel(1), new HitResult(sq.Id, HandleKind.SE), new(203, 97), Bounds, 1f);
        var after = Changes(c.End(new(223, 97))!)[0].After!;
        Assert.Equal(220f, after.Rect.Right, 3);
        Assert.Equal(100f, after.Rect.Bottom, 3);
    }

    [Fact]
    public void Resize_KeepsMinSize()
    {
        var sq = Square(1, 100, 100, 200, 200);
        var c = Make();
        // drag W handle far past the right edge: right edge stays fixed, width = min
        c.Begin(Snap(sq), Sel(1), new HitResult(sq.Id, HandleKind.W), new(100, 150), Bounds, 1f);
        var after = Changes(c.End(new(500, 150))!)[0].After!;
        Assert.Equal(200f, after.Rect.Right, 3);
        Assert.Equal(AnnotationGeometry.MinShapeSizePt, after.Rect.Width, 3);
        Assert.Equal(100f, after.Rect.Bottom, 3);
        Assert.Equal(200f, after.Rect.Top, 3);
    }

    [Fact]
    public void Resize_OnNonResizable_DoesNothing()
    {
        var note = Note(1, 100, 100);
        var c = Make();
        c.Begin(Snap(note), Sel(1), new HitResult(note.Id, HandleKind.SE), new(120, 100), Bounds, 1f);
        Assert.Null(c.End(new(200, 0)));
    }

    [Fact]
    public void DeleteSelected_TwoAnnotations_OneCommand_UndoRestoresBoth()
    {
        var a = Square(1, 0, 0, 10, 10);
        var b = Note(2, 50, 50);
        var other = Square(3, 80, 80, 90, 90);
        var cmd = Make().DeleteSelected(Snap(a, b, other), Sel(1, 2));
        var ch = Changes(cmd!);
        Assert.Equal(2, ch.Length);
        Assert.All(ch, x => Assert.Null(x.After));
        Assert.Equal([a, b], ch.Select(x => x.Before!));
        Assert.Null(Make().DeleteSelected(Snap(a), Sel()));
    }

    // ---- M3a: 새 유형의 손잡이와 묶음 ----

    private static Annotation Line(long id, params (float, float)[] pts)
    {
        var a = new Annotation
        {
            Id = new AnnotId(id), Kind = AnnotKind.Line, BorderWidth = 2f,
            Vertices = [.. pts.Select(p => new PdfPointF(p.Item1, p.Item2))],
        };
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private static Annotation Poly(long id, AnnotKind kind, params (float, float)[] pts)
    {
        var a = Line(id, pts) with { Kind = kind };
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    private static Annotation FreeText(long id, PdfRectF box, params (float, float)[] leader)
    {
        var a = new Annotation
        {
            Id = new AnnotId(id), Kind = AnnotKind.FreeText, BorderWidth = 1f,
            Intent = leader.Length > 0 ? FreeTextIntent.Callout : FreeTextIntent.TextBox,
            Vertices = [.. leader.Select(p => new PdfPointF(p.Item1, p.Item2))],
        };
        a = leader.Length > 0 ? a with { TextBox = box } : a with { Rect = box };
        return a with { Rect = AnnotationGeometry.ComputeRect(a) };
    }

    [Fact]
    public void LineEndpointHandle_MovesOnlyThatEnd()
    {
        var line = Line(1, (100, 100), (300, 200));
        var c = Make();
        var hit = c.HitTest(Snap(line), Sel(1), new(301, 199), 1f);
        Assert.Equal(new HitResult(line.Id, HandleKind.Vertex, 1), hit);
        Assert.Equal(new HitResult(line.Id, HandleKind.Vertex, 0), c.HitTest(Snap(line), Sel(1), new(100, 100), 1f));

        c.Begin(Snap(line), Sel(1), hit!, new(301, 199), Bounds, 1f);   // grabbed 1,-1 off centre
        var cmd = c.End(new(351, 249));
        var ch = Changes(cmd!);
        Assert.Single(ch);
        Assert.Same(line, ch[0].Before);
        var after = ch[0].After!;
        Assert.Equal(new PdfPointF(100, 100), after.Vertices[0]);
        Assert.Equal(new PdfPointF(350, 250), after.Vertices[1]);
        Assert.Equal(AnnotationGeometry.ComputeRect(after), after.Rect);
        Assert.Equal(Now, after.ModifiedAt);

        // unselected line: body drag moves the whole line
        var hitBody = c.HitTest(Snap(line), Sel(), new(200, 150), 1f);
        Assert.Equal(HandleKind.Body, hitBody!.Handle);
        c.Begin(Snap(line), Sel(), hitBody, new(200, 150), Bounds, 1f);
        var moved = Changes(c.End(new(210, 160))!)[0].After!;
        Assert.Equal(new PdfPointF(110, 110), moved.Vertices[0]);
        Assert.Equal(new PdfPointF(310, 210), moved.Vertices[1]);

        // 1 DIP no-op
        c.Begin(Snap(line), Sel(1), hit!, new(301, 199), Bounds, 1f);
        Assert.Null(c.End(new(301.5f, 199.5f)));
    }

    [Fact]
    public void PolygonVertexHandle_MovesOnlyThatVertex()
    {
        var poly = Poly(1, AnnotKind.Polygon, (100, 100), (200, 100), (200, 200), (100, 200));
        Assert.Equal(4, SelectionController.HandlesOf(poly).Count);   // vertices only, no box handles
        var c = Make();
        var hit = c.HitTest(Snap(poly), Sel(1), new(200, 200), 1f);
        Assert.Equal(new HitResult(poly.Id, HandleKind.Vertex, 2), hit);
        c.Begin(Snap(poly), Sel(1), hit!, new(200, 200), Bounds, 1f);
        var after = Changes(c.End(new(260, 240))!)[0].After!;
        Assert.Equal(poly.Vertices[0], after.Vertices[0]);
        Assert.Equal(poly.Vertices[1], after.Vertices[1]);
        Assert.Equal(new PdfPointF(260, 240), after.Vertices[2]);
        Assert.Equal(poly.Vertices[3], after.Vertices[3]);

        // polyline vertices too, and drag is clamped to the page
        var pl = Poly(2, AnnotKind.PolyLine, (10, 10), (50, 50), (90, 10));
        c.Begin(Snap(pl), Sel(2), new HitResult(pl.Id, HandleKind.Vertex, 0), new(10, 10), Bounds, 1f);
        var pa = Changes(c.End(new(-100, 900))!)[0].After!;
        Assert.Equal(new PdfPointF(0, 800), pa.Vertices[0]);
        Assert.Equal(pl.Vertices[1], pa.Vertices[1]);
    }

    [Fact]
    public void StaleVertexHandle_IsIgnored()
    {
        var line = Line(1, (100, 100), (300, 200));
        var c = Make();
        c.Begin(Snap(line), Sel(1), new HitResult(line.Id, HandleKind.Vertex, 5), new(300, 200), Bounds, 1f);
        Assert.Null(c.End(new(400, 400)));
    }

    [Fact]
    public void FreeText_BoxHandles_ResizeBoxOnly_AndLeaderTipMovesTip()
    {
        var box = new PdfRectF(200, 260, 360, 300);
        var co = FreeText(1, box, (100, 100), (200, 280));
        var c = Make();
        Assert.Equal(1 + 8, SelectionController.HandlesOf(co).Count);   // leader tip + 8 box handles

        // N handle sits on the box top (not on the union with the leader)
        var hitN = c.HitTest(Snap(co), Sel(1), new(280, 300), 1f);
        Assert.Equal(HandleKind.N, hitN!.Handle);
        c.Begin(Snap(co), Sel(1), hitN, new(280, 300), Bounds, 1f);
        var a1 = Changes(c.End(new(280, 340))!)[0].After!;
        Assert.Equal(new PdfRectF(200, 260, 360, 340), a1.TextBox);
        Assert.Equal(co.Vertices, a1.Vertices);   // leader unchanged

        // W handle grows the box leftwards while the right edge stays put
        var hitW = c.HitTest(Snap(co), Sel(1), new(200, 280), 1f);
        // (the leader's end vertex sits on this spot but is not a handle; box W handle wins)
        Assert.Equal(HandleKind.W, hitW!.Handle);
        c.Begin(Snap(co), Sel(1), hitW, new(200, 280), Bounds, 1f);
        var a2 = Changes(c.End(new(150, 280))!)[0].After!;
        Assert.Equal(new PdfRectF(150, 260, 360, 300), a2.TextBox);

        // leader tip handle moves only Vertices[0]
        var hitTip = c.HitTest(Snap(co), Sel(1), new(100, 100), 1f);
        Assert.Equal(HandleKind.LeaderTip, hitTip!.Handle);
        c.Begin(Snap(co), Sel(1), hitTip, new(100, 100), Bounds, 1f);
        var a3 = Changes(c.End(new(80, 60))!)[0].After!;
        Assert.Equal(new PdfPointF(80, 60), a3.Vertices[0]);
        Assert.Equal(co.Vertices[1], a3.Vertices[1]);
        Assert.Equal(box, a3.TextBox);

        // plain text box: resize changes Rect
        var tb = FreeText(2, new PdfRectF(100, 100, 300, 140));
        c.Begin(Snap(tb), Sel(2), new HitResult(tb.Id, HandleKind.SE), new(300, 100), Bounds, 1f);
        var a4 = Changes(c.End(new(340, 80))!)[0].After!;
        Assert.Equal(new PdfRectF(100, 80, 340, 140), a4.Rect);
    }

    private static (Annotation Strike, Annotation Caret) Group()
    {
        var strike = new Annotation
        {
            Id = new AnnotId(10), Kind = AnnotKind.StrikeOut,
            Quads = [PdfQuadF.FromRect(new PdfRectF(100, 500, 200, 512))],
        };
        strike = strike with { Rect = AnnotationGeometry.ComputeRect(strike) };
        var caret = new Annotation
        {
            Id = new AnnotId(11), Kind = AnnotKind.Caret, Rect = new PdfRectF(197, 500, 204, 512),
            InReplyTo = strike.Id, ReplyKind = ReplyType.Group,
        };
        return (strike, caret);
    }

    [Fact]
    public void SelectingGroupMember_SelectsBoth()
    {
        var (strike, caret) = Group();
        var other = Square(3, 400, 400, 450, 450);
        var snap = Snap(strike, caret, other);
        var sel = Sel();
        var c = Make();

        // pressing the caret selects the whole group
        c.Begin(snap, sel, new HitResult(caret.Id, HandleKind.Body), new(200, 506), Bounds, 1f);
        Assert.Equal([strike.Id, caret.Id], sel.Ids);

        // moving it is ONE command over both members
        var cmd = c.End(new(220, 516));
        var ch = Changes(cmd!);
        Assert.Equal(2, ch.Length);
        Assert.Same(strike, ch[0].Before);
        Assert.Same(caret, ch[1].Before);
        Assert.Equal(new PdfRectF(120, 510, 220, 522), ch[0].After!.Rect);
        Assert.Equal(strike.Id, ch[1].After!.InReplyTo);   // group link preserved

        // deleting one selected member deletes the whole group in one command
        var del = c.DeleteSelected(snap, Sel(11));
        var dch = Changes(del!);
        Assert.Equal(2, dch.Length);
        Assert.All(dch, x => Assert.Null(x.After));
        Assert.DoesNotContain(dch, x => x.Before!.Id == other.Id);

        // a selected member whose partner is not selected gets the partner added on Begin
        var sel2 = Sel(10);
        c.Begin(snap, sel2, new HitResult(strike.Id, HandleKind.Body), new(150, 506), Bounds, 1f);
        Assert.Equal([strike.Id, caret.Id], sel2.Ids);

        // an ungrouped annotation still selects alone
        var sel3 = Sel();
        c.Begin(snap, sel3, new HitResult(other.Id, HandleKind.Body), new(420, 420), Bounds, 1f);
        Assert.Equal([other.Id], sel3.Ids);
    }

    [Fact]
    public void ExpandGroups_HandlesMissingAndDuplicate()
    {
        var (strike, caret) = Group();
        var snap = Snap(strike, caret);
        Assert.Equal([strike.Id, caret.Id], SelectionController.ExpandGroups(snap, [caret.Id, strike.Id]));
        Assert.Equal([new AnnotId(99)], SelectionController.ExpandGroups(snap, [new AnnotId(99)]));
    }

    [Fact]
    public void Prune_AfterUndoRemovesDeletedIds()
    {
        var a = Square(1, 0, 0, 10, 10);
        var b = Square(2, 0, 0, 10, 10);
        var sel = Sel(1, 2);
        int changed = 0;
        sel.Changed += () => changed++;
        sel.Prune(Snap(a));
        Assert.Equal([a.Id], sel.Ids);
        Assert.Equal(1, changed);
        sel.Prune(Snap(b));
        Assert.Empty(sel.Ids);
        Assert.Null(sel.Page);
    }

    [Fact]
    public void Selection_ToggleAndOtherPage()
    {
        var sel = Sel(1);
        sel.Toggle(Page, new AnnotId(2));
        Assert.Equal([new AnnotId(1), new AnnotId(2)], sel.Ids);
        sel.Toggle(Page, new AnnotId(1));
        Assert.Equal([new AnnotId(2)], sel.Ids);
        sel.Toggle(new PageId(8), new AnnotId(5));
        Assert.Equal(new PageId(8), sel.Page);
        Assert.Equal([new AnnotId(5)], sel.Ids);
        sel.Clear();
        Assert.Empty(sel.Ids);
    }
}
