using Avalonia.Headless;
using Avalonia.Input;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Commands;
using PdfEditor.TestFixtures;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Objects;

/// <summary>2026-10-20 사용자 요청: 빈 곳에서 끌어 여러 주석 고르기, 스페이스바를 누르는 동안 손(이동) 도구.</summary>
[Collection("TabSettings")]
public class MarqueeAndSpaceTests : ObjectUiTestBase
{
    private Annotation Square(DocumentSession s, float l, float b, float r, float t) => new()
    {
        Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(l, b, r, t),
        Color = new PdfColor(220, 30, 30), BorderWidth = 2f, Name = Guid.NewGuid().ToString("D"),
    };

    [Fact]
    public async Task Marquee_SelectsAllOverlappingAnnotations_ClickOnEmptyClears()
    {
        var s = await OpenAsync(p => ContentPdfFactory.CreateDocumentLike(p));
        await s.AnnotationsReady;
        var page = s.Pages[0].Id;
        // 위쪽에서 y = 842-pt: 화면 위에서 (100,500)~(160,540), (220,500)~(280,540), 멀리 떨어진 (400,700)~(450,740)
        var a = Square(s, 100, 842 - 540, 160, 842 - 500);
        var b = Square(s, 220, 842 - 540, 280, 842 - 500);
        var far = Square(s, 400, 842 - 740, 450, 842 - 700);
        await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(page, [new AnnotationChange(null, a), new AnnotationChange(null, b), new AnnotationChange(null, far)]));
        var h = Show(s);
        SetTool(h, ToolKind.AnnotSelect);
        // 빈 곳(60,470)에서 (300,560)까지 끌기: a·b만 겹친다
        Drag(h, AtPt(h, 60, 470), AtPt(h, 300, 560));
        PumpUi(() => h.Tab.Selection.Ids.Count == 2);
        Assert.Contains(a.Id, h.Tab.Selection.Ids);
        Assert.Contains(b.Id, h.Tab.Selection.Ids);
        Assert.DoesNotContain(far.Id, h.Tab.Selection.Ids);
        // 빈 곳을 그냥 누르면 선택이 풀린다
        Click(h, AtPt(h, 500, 300));
        PumpUi(() => h.Tab.Selection.Ids.Count == 0);
    }

    [Fact]
    public async Task SpaceBar_HoldsHandTool_ReleaseRestores()
    {
        var s = await OpenAsync(p => ContentPdfFactory.CreateDocumentLike(p));
        var h = Show(s);
        SetTool(h, ToolKind.AnnotSelect);
        HeadlessUi.Run(() => { h.View.Focus(); h.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " "); return 0; });
        Assert.Equal(ToolKind.Hand, h.Tab.Tool);            // 누르는 동안 손 도구
        HeadlessUi.Run(() => { h.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " "); return 0; });
        Assert.Equal(ToolKind.AnnotSelect, h.Tab.Tool);
    }

    [Fact]
    public async Task AnnotSelectTool_ClickOnInsertedShape_SelectsItAndKeepsTheTool()
    {
        var s = await OpenAsync(p => ContentPdfFactory.CreateDocumentLike(p));
        await s.AnnotationsReady;
        var page = s.Pages[0].Id;
        var spec = ObjectToolController.MakeShape(PdfEditor.Document.Content.ShapeKind.Rectangle, new PdfPointF(300, 300), new PdfPointF(400, 360))!;
        var edit = new PdfEditor.Document.Content.InsertShapeEdit(s.Content.NewId(), spec);
        await s.Commands.ExecuteAsync(new PdfEditor.Document.Content.ContentEditCommand(page, [edit]));
        var h = Show(s);
        SetTool(h, ToolKind.AnnotSelect);
        // 객체 목록이 올 때까지 화면을 그리게 한다
        HeadlessUi.Run(() =>
        {
            var st = typeof(PdfEditor.App.Viewer.DocumentView).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(h.View)!;
            var idx = (System.Collections.IDictionary)st.GetType().GetField("ObjIndex")!.GetValue(st)!;
            Pump(() => { h.View.InvalidateVisual(); h.Window.UpdateLayout(); _ = h.Window.CaptureRenderedFrame(); return idx.Count > 0; });
            return 0;
        });
        Click(h, AtPt(h, 300, 842 - 330));   // 도형의 왼쪽 변 위(속 빈 도형은 선 근처를 눌러야 맞는다 — 안쪽은 아님)
        PumpUi(() => h.Tab.Objects.Selected is not null);
        Assert.Equal(ToolKind.AnnotSelect, h.Tab.Tool);   // 도구는 주석 선택 그대로(2026-10-20 사용자 요청)
        Assert.Equal(PdfEditor.Document.Content.ObjectRef.Inserted(edit.Id), h.Tab.Objects.Selected!.Ref);
    }

    private static void WarmIndex(Host h) => HeadlessUi.Run(() =>
    {
        var st = typeof(PdfEditor.App.Viewer.DocumentView).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(h.View)!;
        var idx = (System.Collections.IDictionary)st.GetType().GetField("ObjIndex")!.GetValue(st)!;
        Pump(() => { h.View.InvalidateVisual(); h.Window.UpdateLayout(); _ = h.Window.CaptureRenderedFrame(); return idx.Count > 0; });
        return 0;
    });

    private async Task<(Host H, PdfEditor.Document.Content.InsertShapeEdit A, PdfEditor.Document.Content.InsertShapeEdit B)> TwoShapesAsync(ToolKind tool)
    {
        var s = await OpenAsync(p => ContentPdfFactory.CreateDocumentLike(p));
        await s.AnnotationsReady;
        var page = s.Pages[0].Id;
        var a = new PdfEditor.Document.Content.InsertShapeEdit(s.Content.NewId(), ObjectToolController.MakeShape(PdfEditor.Document.Content.ShapeKind.Rectangle, new PdfPointF(300, 300), new PdfPointF(360, 340))!);
        var b = new PdfEditor.Document.Content.InsertShapeEdit(s.Content.NewId(), ObjectToolController.MakeShape(PdfEditor.Document.Content.ShapeKind.Ellipse, new PdfPointF(400, 300), new PdfPointF(460, 340))!);
        await s.Commands.ExecuteAsync(new PdfEditor.Document.Content.ContentEditCommand(page, [a, b]));
        var h = Show(s);
        SetTool(h, tool);
        WarmIndex(h);
        return (h, a, b);
    }

    [Fact]
    public async Task AnnotSelectMarquee_OverInsertedShapes_SelectsBothAsObjects_MoveAndDeleteAreOneCommand()
    {
        var (h, a, b) = await TwoShapesAsync(ToolKind.AnnotSelect);
        // 빈 곳(260,480)에서 (500,560)까지: 두 도형(쪽 아래 기준 y 300~340 = 위 기준 502~542)과 겹친다
        Drag(h, AtPt(h, 260, 480), AtPt(h, 500, 560));
        PumpUi(() => h.Tab.Objects.All.Count == 2);
        Assert.Equal(ToolKind.AnnotSelect, h.Tab.Tool);
        Assert.Equal([PdfEditor.Document.Content.ObjectRef.Inserted(a.Id), PdfEditor.Document.Content.ObjectRef.Inserted(b.Id)], h.Tab.Objects.All.Select(x => x.Ref).OrderBy(x => x.InsertedId).ToArray());
        // 한 도형 위에서 끌면 둘 다 같은 양만큼 옮겨지고 명령은 하나
        int before = Edits(h).Count;
        Drag(h, AtPt(h, 330, 522), AtPt(h, 360, 522));   // 왼쪽 도형 가운데를 30pt 오른쪽으로
        PumpUi(() => Edits(h).Count == before + 2);
        var moves = Edits(h).Skip(before).Cast<PdfEditor.Document.Content.TransformEdit>().ToList();
        Assert.All(moves, m => Assert.Equal(30, m.Then.E, 1f));
        await h.Session.Commands.UndoAsync();
        Assert.Equal(before, Edits(h).Count);   // 실행 취소 한 번에 둘 다 원래대로
        // 지우기도 한 명령
        PumpUi(() => h.Tab.Objects.All.Count == 2);
        HeadlessUi.Run(() => { h.View.Focus(); h.Window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null); return 0; });
        PumpUi(() => Edits(h).Count == before + 2);
        Assert.All(Edits(h).Skip(before), e => Assert.IsType<PdfEditor.Document.Content.DeactivateEdit>(e));
    }

    [Fact]
    public async Task ObjectEditMarquee_SelectsOriginalPageObjectsOnly_NeverAnnotationsOrInsertedOnes()
    {
        var (h, a, b) = await TwoShapesAsync(ToolKind.EditObject);
        var page = h.Session.Pages[0].Id;
        var sq = Square(h.Session, 100, 842 - 540, 160, 842 - 500);
        await h.Session.Commands.ExecuteAsync(new ChangeAnnotationsCommand(page, [new AnnotationChange(null, sq)]));
        // 쪽 전체를 감싸는 끌기: 원래 쪽 내용(글자·이미지·도형)만 고르고, 주석과 넣은 도형은 고르지 않는다
        Drag(h, AtPt(h, 5, 5), AtPt(h, 590, 835));
        PumpUi(() => h.Tab.Objects.All.Count > 0);
        Assert.All(h.Tab.Objects.All, x => Assert.False(x.Ref.IsInserted));
        Assert.Empty(h.Tab.Selection.Ids);   // 주석은 선택되지 않는다
    }

    [Fact]
    public async Task AnnotSelectMarquee_SelectsAnnotationsAndInsertedObjectsTogether_AndMovesBoth()
    {
        var (h, a, b) = await TwoShapesAsync(ToolKind.AnnotSelect);
        var page = h.Session.Pages[0].Id;
        var sq = Square(h.Session, 100, 842 - 540, 160, 842 - 500);
        await h.Session.Commands.ExecuteAsync(new ChangeAnnotationsCommand(page, [new AnnotationChange(null, sq)]));
        PumpUi(() => true);
        // 주석(100~160, 위 기준 500~540)과 도형 둘(300~460)을 모두 감싸는 끌기
        Drag(h, AtPt(h, 60, 470), AtPt(h, 500, 560));
        await Task.Delay(500); PumpUi(() => true);
        Assert.True(h.Tab.Selection.Ids.Count == 1 && h.Tab.Objects.All.Count == 2, $"ann={h.Tab.Selection.Ids.Count} obj={h.Tab.Objects.All.Count}");
        Assert.Equal(ToolKind.AnnotSelect, h.Tab.Tool);
        // 고른 주석 위에서 끌면 함께 고른 객체도 같은 양만큼
        int before = Edits(h).Count;
        Drag(h, AtPt(h, 100, 520), AtPt(h, 130, 520));   // 네모 주석은 테두리에서만 잡힌다
        await Task.Delay(700); PumpUi(() => true);
        Assert.True(Edits(h).Count == before + 2, $"edits {Edits(h).Count - before}, objs={h.Tab.Objects.All.Count}, ann={h.Tab.Selection.Ids.Count}, rect={h.Session.Annotations.Get(page).Items.First(v => v.Data.Id == sq.Id).Data.Rect}");
        Assert.All(Edits(h).Skip(before), e => Assert.Equal(30, ((PdfEditor.Document.Content.TransformEdit)e).Then.E, 1f));
        var moved = h.Session.Annotations.Get(page).Items.First(v => v.Data.Id == sq.Id).Data.Rect;
        Assert.Equal(130f, moved.Left, 1f);   // 주석도 30pt 옮겨짐
    }
}
