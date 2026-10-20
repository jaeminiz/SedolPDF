using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Viewer.Annotations;

public class NewToolsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly PageId Page = new(7);
    private static readonly ToolStyle Style = new(PdfColor.FromHex("#E5372D"), null, 1f, 1.5f);
    private long _id;
    private int _name;

    private AnnotationToolController Make() => new(() => new AnnotId(++_id), () => Now, () => "me", t => "subj-" + t)
    {
        CanUsePatchFeatures = () => true,
        FitTypewriter = a => a,
        NewName = () => "nm-" + (++_name),
        StampLabel = s => "label-" + s.BuiltinName,
    };

    private static AnnotationChange[] Changes(ChangeAnnotationsCommand? cmd)
    {
        Assert.NotNull(cmd);
        return (AnnotationChange[])typeof(ChangeAnnotationsCommand)
            .GetField("_changes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cmd)!;
    }

    private static Annotation Added(ChangeAnnotationsCommand? cmd)
    {
        var ch = Changes(cmd);
        Assert.Single(ch);
        Assert.Null(ch[0].Before);
        return ch[0].After!;
    }

    private static void CheckIdentity(Annotation a, ToolKind tool)
    {
        Assert.False(string.IsNullOrEmpty(a.Name));
        Assert.Equal("me", a.Author);
        Assert.Equal("subj-" + tool, a.Subject);
        Assert.Equal(Now, a.CreatedAt);
        Assert.Equal(PdfAnnotFlags.Print, a.Flags);
    }

    [Fact]
    public void Line_ShiftSnaps15Degrees()
    {
        var c = Make();
        c.Begin(ToolKind.Line, Style, Page, new(100, 100), 1f);
        c.Move(new(200, 130), shift: true);   // atan(30/100) = 16.7 deg -> 15 deg
        var a = Added(c.End(new(200, 130), shift: true));
        Assert.Equal(AnnotKind.Line, a.Kind);
        var v = a.Vertices;
        Assert.Equal(new PdfPointF(100, 100), v[0]);
        float len = MathF.Sqrt(100 * 100 + 30 * 30);
        Assert.Equal(100 + len * MathF.Cos(15 * MathF.PI / 180), v[1].X, 2);
        Assert.Equal(100 + len * MathF.Sin(15 * MathF.PI / 180), v[1].Y, 2);
        Assert.Equal(LineEnding.None, a.EndEnding);
        CheckIdentity(a, ToolKind.Line);

        // without Shift the end point is exact
        c.Begin(ToolKind.Line, Style, Page, new(100, 100), 1f);
        var free = Added(c.End(new(200, 130)));
        Assert.Equal(new PdfPointF(200, 130), free.Vertices[1]);
    }

    [Fact]
    public void Line_UnderOneDip_CreatesNothing_AndArrowHasOpenArrowEnd()
    {
        var c = Make();
        c.Begin(ToolKind.Line, Style, Page, new(10, 10), 2f);
        Assert.Null(c.End(new(11, 10.5f)));   // 1.1 pt < 1 DIP * 2 pt/DIP

        c.Begin(ToolKind.Arrow, Style, Page, new(10, 10), 1f);
        var a = Added(c.End(new(60, 10)));
        Assert.Equal(AnnotKind.Line, a.Kind);
        Assert.Equal(LineEnding.OpenArrow, a.EndEnding);
        CheckIdentity(a, ToolKind.Arrow);
        Assert.Equal(AnnotationGeometry.ComputeRect(a), a.Rect);
    }

    [Fact]
    public void PolyLine_DoubleClickFinishes_BackspaceRemovesLast()
    {
        var c = Make();
        c.Begin(ToolKind.PolyLine, Style, Page, new(10, 10), 1f);
        Assert.Null(c.End(new(10, 10)));
        c.Move(new(50, 10));
        c.Begin(ToolKind.PolyLine, Style, Page, new(50, 10), 1f);
        c.End(new(50, 10));
        c.Begin(ToolKind.PolyLine, Style, Page, new(50, 60), 1f);
        c.End(new(50, 60));
        Assert.Equal(3, c.CollectedPointCount);

        c.RemoveLastPoint();
        Assert.Equal(2, c.CollectedPointCount);

        // double click = two clicks on the same spot, then finish
        c.Begin(ToolKind.PolyLine, Style, Page, new(90, 60), 1f);
        c.End(new(90, 60));
        c.Begin(ToolKind.PolyLine, Style, Page, new(90, 60), 1f);
        c.End(new(90, 60));
        var a = Added(c.FinishPolyline());
        Assert.Equal(AnnotKind.PolyLine, a.Kind);
        Assert.Equal([new PdfPointF(10, 10), new PdfPointF(50, 10), new PdfPointF(90, 60)], a.Vertices.ToArray());
        CheckIdentity(a, ToolKind.PolyLine);
        Assert.False(c.IsCollectingPoints);
        Assert.Null(c.Preview);
    }

    [Fact]
    public void PolyLine_EscCancelsAll_AndOnePointCreatesNothing()
    {
        var c = Make();
        c.Begin(ToolKind.PolyLine, Style, Page, new(10, 10), 1f);
        c.End(new(10, 10));
        Assert.Null(c.FinishPolyline());   // 1 point
        Assert.False(c.IsCollectingPoints);

        c.Begin(ToolKind.PolyLine, Style, Page, new(10, 10), 1f);
        c.Begin(ToolKind.PolyLine, Style, Page, new(40, 40), 1f);
        c.Cancel();
        Assert.Null(c.FinishPolyline());
        Assert.Equal(0, c.CollectedPointCount);
    }

    [Fact]
    public void Polygon_TwoPoints_CreatesNothing()
    {
        var c = Make();
        c.Begin(ToolKind.Polygon, Style, Page, new(10, 10), 1f);
        c.Begin(ToolKind.Polygon, Style, Page, new(60, 10), 1f);
        Assert.Null(c.FinishPolyline());

        c.Begin(ToolKind.Polygon, Style, Page, new(10, 10), 1f);
        c.Begin(ToolKind.Polygon, Style, Page, new(60, 10), 1f);
        c.Begin(ToolKind.Polygon, Style, Page, new(60, 60), 1f);
        var a = Added(c.FinishPolyline());
        Assert.Equal(AnnotKind.Polygon, a.Kind);
        Assert.Equal(3, a.Vertices.Length);
    }

    [Fact]
    public void Cloud_Drag_CreatesSquareWithIntensityOne()
    {
        var c = Make();
        c.Begin(ToolKind.Cloud, Style, Page, new(10, 10), 1f);
        var a = Added(c.End(new(110, 60)));
        Assert.Equal(AnnotKind.Square, a.Kind);
        Assert.Equal(1f, a.CloudIntensity);
        Assert.Equal(new PdfRectF(10, 10, 110, 60), a.Rect);
        CheckIdentity(a, ToolKind.Cloud);

        c.Begin(ToolKind.Cloud, Style, Page, new(10, 10), 1f);
        Assert.Null(c.End(new(11, 11)));
    }

    [Fact]
    public void TextBox_TinyDrag_DefaultSize()
    {
        var c = Make();
        c.Begin(ToolKind.TextBox, Style, Page, new(100, 300), 1f);
        var a = Added(c.End(new(101, 299)));
        Assert.Equal(AnnotKind.FreeText, a.Kind);
        Assert.Equal(FreeTextIntent.TextBox, a.Intent);
        Assert.Equal(new PdfRectF(100, 260, 300, 300), a.Rect);   // 200 x 40, click = top-left
        Assert.Equal(a, c.LastCreatedEdit);
        CheckIdentity(a, ToolKind.TextBox);

        c.Begin(ToolKind.TextBox, Style, Page, new(100, 300), 1f);
        var big = Added(c.End(new(220, 220)));
        Assert.Equal(new PdfRectF(100, 220, 220, 300), big.Rect);
    }

    [Fact]
    public void Typewriter_Click_IsFittedBeforeCommit_AndEditOpens()
    {
        var c = Make();
        int fitted = 0;
        c.FitTypewriter = a => { fitted++; return a with { Rect = new PdfRectF(a.Rect.Left, a.Rect.Top - 17, a.Rect.Left + 33, a.Rect.Top) }; };
        c.Begin(ToolKind.Typewriter, Style, Page, new(50, 500), 1f);
        var a = Added(c.End(new(50, 500)));
        Assert.True(fitted >= 1);
        Assert.Equal(FreeTextIntent.Typewriter, a.Intent);
        Assert.Equal(new PdfRectF(50, 483, 83, 500), a.Rect);
        Assert.Equal(a, c.LastCreatedEdit);
        CheckIdentity(a, ToolKind.Typewriter);

        // text changed in the editor -> FitForCommit refits typewriters only
        int before = fitted;
        Assert.NotEqual(a.Rect, c.FitForCommit(a with { Contents = "x", Rect = default }).Rect);
        Assert.Equal(before + 1, fitted);
        var tb = a with { Intent = FreeTextIntent.TextBox };
        Assert.Same(tb, c.FitForCommit(tb));
    }

    [Fact]
    public void Callout_PointThenBox()
    {
        var c = Make();
        c.Begin(ToolKind.Callout, Style, Page, new(100, 100), 1f);
        var a = Added(c.End(new(200, 300)));
        Assert.Equal(FreeTextIntent.Callout, a.Intent);
        Assert.Equal(new PdfPointF(100, 100), a.Vertices[0]);           // pressed point = leader tip
        Assert.Equal(new PdfRectF(200, 260, 360, 300), a.TextBox);     // released point = box top-left, 160 x 40
        Assert.InRange(a.Vertices.Length, 2, 3);
        Assert.Equal(LineEnding.OpenArrow, a.EndEnding);
        Assert.Equal(AnnotationGeometry.ComputeRect(a), a.Rect);
        Assert.Equal(a, c.LastCreatedEdit);
        CheckIdentity(a, ToolKind.Callout);
    }

    [Fact]
    public void CalloutLeader_IsClampedToTwoOrThreePoints()
    {
        Assert.Null(AnnotationToolController.NormalizeCalloutLeader([new(0, 0)]));
        Assert.Equal(2, AnnotationToolController.NormalizeCalloutLeader([new(0, 0), new(5, 5)])!.Value.Length);
        var three = AnnotationToolController.NormalizeCalloutLeader([new(0, 0), new(1, 1), new(2, 2), new(3, 3), new(4, 4)])!.Value;
        Assert.Equal(3, three.Length);
        Assert.Equal(new PdfPointF(0, 0), three[0]);
        Assert.Equal(new PdfPointF(4, 4), three[2]);
    }

    [Fact]
    public void InsertText_CaretAtGlyphBoundary()
    {
        var c = Make();
        double seenTol = 0;
        c.CharBoundaryAt = (page, p, tol) =>
        {
            Assert.Equal(Page, page);
            seenTol = tol;
            return new CharBoundary(52f, 700f, 12f);
        };
        c.Begin(ToolKind.InsertText, Style, Page, new(51, 705), 2f);
        Assert.Null(c.Preview);
        var a = Added(c.End(new(51, 705)));
        Assert.Equal(AnnotKind.Caret, a.Kind);
        Assert.Equal(12f, a.Rect.Height, 3);
        Assert.Equal(700f, a.Rect.Bottom, 3);
        Assert.Equal(52f, (a.Rect.Left + a.Rect.Right) / 2f, 3);
        Assert.Equal(8.0, seenTol);   // 4 DIP * 2 pt/DIP
        Assert.Equal(a, c.LastCreatedEdit);
        CheckIdentity(a, ToolKind.InsertText);

        // no glyph near the click -> nothing
        c.CharBoundaryAt = (_, _, _) => null;
        c.Begin(ToolKind.InsertText, Style, Page, new(300, 300), 1f);
        Assert.Null(c.End(new(300, 300)));
    }

    [Fact]
    public void ReplaceText_CreatesGroup()
    {
        var c = Make();
        var rects = new[] { new PdfRectF(50, 700, 150, 712), new PdfRectF(50, 686, 90, 698) };
        var cmd = c.CreateReplace(Style, Page, rects);
        var ch = Changes(cmd);
        Assert.Equal(2, ch.Length);
        Assert.All(ch, x => Assert.Null(x.Before));
        var strike = ch[0].After!;
        var caret = ch[1].After!;
        Assert.Equal(AnnotKind.StrikeOut, strike.Kind);
        Assert.Equal(2, strike.Quads.Length);
        Assert.Equal(AnnotKind.Caret, caret.Kind);
        Assert.Equal(strike.Id, caret.InReplyTo);
        Assert.Equal(ReplyType.Group, caret.ReplyKind);
        // caret sits at the end of the selection, as tall as the last glyph row
        Assert.Equal(12f, caret.Rect.Height, 3);
        Assert.Equal(90f, (caret.Rect.Left + caret.Rect.Right) / 2f, 3);
        Assert.Equal(caret, c.LastCreatedEdit);
        Assert.NotEqual(strike.Name, caret.Name);
        CheckIdentity(strike, ToolKind.ReplaceText);
        CheckIdentity(caret, ToolKind.ReplaceText);

        Assert.Null(c.CreateReplace(Style, Page, []));
    }

    /// <summary>2026-10-20 버그: 회전된 쪽에서 만든 글자 주석은 쪽 회전만큼 Rotation을 갖고, 기본 크기도 화면 기준이다.</summary>
    [Fact]
    public void RotatedPage_TextBoxAndStamp_GetRotation()
    {
        var c = Make();
        c.PageRotationOf = _ => 1;   // 시계 방향 90도로 보이는 쪽
        c.Begin(ToolKind.TextBox, Style, Page, new(100, 300), 1f);
        var a = Added(c.End(new(101, 299)));
        Assert.Equal(90, a.Rotation);
        // 화면에서 200 × 40(가로로 긴 상자) = 쪽 좌표 40 × 200, 클릭한 점이 화면 왼쪽 위(쪽 좌표의 왼쪽 아래)
        Assert.Equal(new PdfRectF(100, 300, 140, 500), a.Rect);

        c.Begin(ToolKind.Stamp, Style, Page, new(300, 400), 1f);
        var st = Added(c.End(new(300, 400)));
        Assert.Equal(90, st.Rotation);
        Assert.Equal(50f, st.Rect.Width, 3);
        Assert.Equal(150f, st.Rect.Height, 3);

        c.PageRotationOf = _ => 0;
        c.Begin(ToolKind.Stamp, Style, Page, new(300, 400), 1f);
        Assert.Equal(0, Added(c.End(new(300, 400))).Rotation);
    }

    [Fact]
    public void Stamp_DefaultSizeAtClick()
    {
        var c = Make();
        c.Begin(ToolKind.Stamp, Style, Page, new(300, 400), 1f);
        var a = Added(c.End(new(300, 400)));
        Assert.Equal(AnnotKind.Stamp, a.Kind);
        Assert.Equal(150f, a.Rect.Width, 3);
        Assert.Equal(50f, a.Rect.Height, 3);
        Assert.Equal(300f, (a.Rect.Left + a.Rect.Right) / 2f, 3);
        Assert.Equal(400f, (a.Rect.Bottom + a.Rect.Top) / 2f, 3);
        Assert.Equal(new StampSpec(StampKind.Builtin, "Approved", null, null), a.Stamp);
        Assert.Equal("label-Approved", a.Contents);
        CheckIdentity(a, ToolKind.Stamp);

        // last chosen stamp is used next time
        c.LastStamp = new StampSpec(StampKind.Builtin, "Draft", null, null);
        c.Begin(ToolKind.Stamp, Style, Page, new(300, 400), 1f);
        Assert.Equal("Draft", Added(c.End(new(300, 400))).Stamp!.BuiltinName);
    }

    [Fact]
    public void FileAttach_ClickThenFileChosen_UsesSharedAssets()
    {
        var c = Make();
        var assets = new AnnotationAssets();
        c.Assets = () => assets;
        c.Begin(ToolKind.FileAttach, Style, Page, new(200, 300), 1f);
        Assert.Null(c.End(new(200, 300)));
        Assert.NotNull(c.PendingAttach);

        var a = Added(c.AttachFile("memo.txt", [1, 2, 3]));
        Assert.Equal(AnnotKind.FileAttachment, a.Kind);
        Assert.Equal("memo.txt", a.AttachmentName);
        Assert.True(assets.HasFile(a.AttachmentId!));
        Assert.Equal(new PdfRectF(200, 280, 220, 300), a.Rect);
        CheckIdentity(a, ToolKind.FileAttach);
        Assert.Null(c.PendingAttach);
        Assert.Null(c.AttachFile("again.txt", [1]));   // no pending click
    }

    [Fact]
    public void FileAttach_TooLarge_IsRejectedNotThrown()
    {
        var c = Make();
        c.Assets = () => new AnnotationAssets();
        c.Begin(ToolKind.FileAttach, Style, Page, new(200, 300), 1f);
        c.End(new(200, 300));
        Assert.Null(c.AttachFile("big.bin", new byte[AnnotationAssets.MaxFileBytes + 1]));
        Assert.True(c.LastAttachRejected);
    }

    [Fact]
    public void PatchTools_DisabledWhenCannotPatch()
    {
        var patchTools = Enum.GetValues<ToolKind>().Where(AnnotationToolController.IsPatchTool).ToList();
        Assert.Equal(12, patchTools.Count);
        var c = Make();
        c.CanUsePatchFeatures = () => false;
        c.CharBoundaryAt = (_, _, _) => new CharBoundary(10, 10, 10);
        foreach (var tool in patchTools)
        {
            c.Begin(tool, Style, Page, new(10, 10), 1f);
            Assert.Null(c.Preview);
            Assert.False(c.IsCollectingPoints);
            Assert.Null(c.End(new(200, 200)));
        }
        Assert.Null(c.FinishPolyline());
        Assert.Null(c.PendingAttach);
        Assert.Null(c.CreateReplace(Style, Page, [new PdfRectF(0, 0, 10, 10)]));
        Assert.Null(c.CreateInsertCaret(Style, Page, new CharBoundary(5, 5, 10)));

        // M2 tools are unaffected
        c.Begin(ToolKind.Rectangle, Style, Page, new(10, 10), 1f);
        Assert.NotNull(c.End(new(100, 100)));
        Assert.NotNull(c.CreateMarkup(ToolKind.StrikeOut, Style, Page, [new PdfRectF(0, 0, 10, 10)]));
    }

    [Fact]
    public void UnwiredController_RefusesPatchTools_AndFailsLoudly()
    {
        var c = new AnnotationToolController(() => new AnnotId(++_id), () => Now, () => "me", t => "subj-" + t);
        foreach (var tool in Enum.GetValues<ToolKind>().Where(AnnotationToolController.IsPatchTool))
        {
            c.Begin(tool, Style, Page, new(10, 10), 1f);
            Assert.Null(c.Preview);
            Assert.Null(c.End(new(100, 100)));
        }
        // M2 tools do not depend on wiring
        c.Begin(ToolKind.Rectangle, Style, Page, new(10, 10), 1f);
        Assert.NotNull(c.End(new(100, 100)));

        // patch allowed but Fit/Assets forgotten -> exception, not silent misbehaviour
        c.CanUsePatchFeatures = () => true;
        Assert.Throws<InvalidOperationException>(() => c.Begin(ToolKind.Typewriter, Style, Page, new(10, 10), 1f));
        c.Cancel();
        c.Begin(ToolKind.FileAttach, Style, Page, new(10, 10), 1f);
        c.End(new(10, 10));
        Assert.Throws<InvalidOperationException>(() => c.AttachFile("a.txt", [1]));
        Assert.False(c.LastAttachRejected);
    }

    [Fact]
    public void PendingAttach_ClearedByOtherTool_AndByCancelAttach()
    {
        var c = Make();
        c.Begin(ToolKind.FileAttach, Style, Page, new(10, 10), 1f);
        c.End(new(10, 10));
        Assert.NotNull(c.PendingAttach);
        c.Begin(ToolKind.Rectangle, Style, Page, new(10, 10), 1f);
        Assert.Null(c.PendingAttach);
        c.Cancel();
        c.Begin(ToolKind.FileAttach, Style, Page, new(10, 10), 1f);
        c.End(new(10, 10));
        c.CancelAttach();
        Assert.Null(c.PendingAttach);
    }

    [Fact]
    public void TextBox_ThinWideDrag_StaysRealBox()
    {
        var c = Make();
        c.Begin(ToolKind.TextBox, Style, Page, new(100, 300), 1f);
        var a = Added(c.End(new(400, 299)));   // 300 x 1: not a click
        Assert.Equal(new PdfRectF(100, 299, 400, 300), a.Rect);
    }

    [Fact]
    public void Stamp_NearPageEdge_IsClampedInsidePage()
    {
        var c = Make();
        c.PageBoundsOf = _ => new PdfRectF(0, 0, 600, 800);
        c.Begin(ToolKind.Stamp, Style, Page, new(10, 790), 1f);
        Assert.Equal(new PdfRectF(0, 750, 150, 800), Added(c.End(new(10, 790))).Rect);
        c.Begin(ToolKind.Stamp, Style, Page, new(595, 5), 1f);
        Assert.Equal(new PdfRectF(450, 0, 600, 50), Added(c.End(new(595, 5))).Rect);
    }

    [Fact]
    public void M2Tools_AlsoGetStableName()
    {
        var c = Make();
        c.Begin(ToolKind.Rectangle, Style, Page, new(10, 10), 1f);
        var a = Added(c.End(new(100, 100)));
        Assert.StartsWith("nm-", a.Name);
    }

    [Fact]
    public void ToolDefaults_NewKinds_HaveStyles()
    {
        foreach (var t in Enum.GetValues<ToolKind>().Where(AnnotationToolController.IsPatchTool))
        {
            var s = ToolDefaults.For(t);
            Assert.True(s.Width >= 0f);
        }
        Assert.Equal(PdfColor.FromHex("#E5372D"), ToolDefaults.For(ToolKind.Line).Color);
        Assert.Equal(1.5f, ToolDefaults.For(ToolKind.Arrow).Width);
        Assert.Equal(ToolDefaults.For(ToolKind.Rectangle), ToolDefaults.For(ToolKind.Rectangle));
    }
}
