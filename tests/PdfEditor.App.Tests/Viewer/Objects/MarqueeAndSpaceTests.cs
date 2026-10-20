using Avalonia.Headless;
using Avalonia.Input;
using Pdf.Native;
using PdfEditor.App.Documents;
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
}
