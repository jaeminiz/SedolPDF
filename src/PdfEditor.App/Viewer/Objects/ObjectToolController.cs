using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.TextEdit;

namespace PdfEditor.App.Viewer.Objects;

/// <summary>한 번의 편집 결과: 화면이 보일 안내(Notice는 대화상자, Toast는 잠깐 뜨는 알림)와 새로 넣은 객체.</summary>
public sealed record ObjectToolResult(string? Notice, string? Toast, ObjectRef? Created = null, PdfRectF? CreatedBounds = null, PdfPageObjectType? CreatedType = null)
{
    public static ObjectToolResult None { get; } = new(null, null);
}

/// <summary>
/// 객체 도구의 계산(Avalonia 없음, M4 Task 8·9): 텍스트 한 줄 바꾸기·텍스트 추가·이미지 넣기·도형 넣기.
/// 계획은 PDFium 전용 스레드에서 세우고(TextEditPlanner), 실제 변경은 ContentEditCommand 하나로 한다(실행 취소 한 번).
/// 문구는 호출한 화면이 보인다.
/// </summary>
public static class ObjectToolController
{
    public const float DefaultTextSizePt = 12f;

    /// <summary>쪽 /Rotate(0~3)만큼 글이 바로 서 보이도록 기준선 왼쪽 점에 놓는 행렬(클릭한 점이 글자 상자의 왼쪽 위가 되게 기준선을 내린다).</summary>
    public static PdfMatrix TextPlacement(PdfPointF clickTopLeft, float sizePt, int pageRotationQuarter)
    {
        var rot = AnnotationRotation.About(new PdfPointF(0, 0), pageRotationQuarter);
        var off = rot.Apply(new PdfPointF(0, -0.8f * sizePt));
        return rot.Then(PdfMatrix.Translate(clickTopLeft.X + off.X, clickTopLeft.Y + off.Y));
    }

    public static string RefusalMessage(ReplacePlan p) => p.Refusal switch
    {
        ReplaceRefusal.Encrypted => Strings.Msg_TextRefusedEncrypted,
        ReplaceRefusal.NoAppFont => Strings.Msg_TextRefusedNoFont,
        ReplaceRefusal.AppFontMissingGlyphs => string.Format(Strings.Msg_TextRefusedMissingGlyphs,
            string.Join(" ", p.MissingCodepoints.Select(char.ConvertFromUtf32))),
        _ => Strings.Msg_TextRefusedNotText,
    };

    /// <summary>텍스트 객체(또는 앱 글꼴 줄)의 전체 글. 쪽이 없어졌으면 null.</summary>
    public static async Task<string?> ReadTextAsync(DocumentTabViewModel tab, PageId page, ObjectRef target)
    {
        if (tab.Session is not { } s) return null;
        try { return await s.Thread.InvokeAsync(() => s.IndexOf(page) < 0 ? null : s.GetObjectText(page, target)); }
        catch (Exception) { return null; }
    }

    /// <summary>텍스트 한 줄 바꾸기. 원래 글꼴이 안 되면 앱 글꼴(알림), 그것도 안 되면 거절 이유(대화상자).</summary>
    public static async Task<ObjectToolResult> ReplaceTextAsync(DocumentTabViewModel tab, PageId page, ObjectRef target, string newText)
    {
        if (tab.Session is not { } s || !tab.CanEditContent) return ObjectToolResult.None;
        try
        {
            var font = DocumentTabViewModel.Appearance?.Font;
            var plan = await s.Thread.InvokeAsync(() => s.IndexOf(page) < 0
                ? ReplacePlan.Refused(ReplaceRefusal.NotText)
                : TextEditPlanner.PlanReplace(s, page, target, newText, font));
            if (plan.Edits.Count == 0) return new ObjectToolResult(RefusalMessage(plan), null);
            await tab.RunContentCommandAsync(new ContentEditCommand(page, plan.Edits));
            return new ObjectToolResult(null, plan.FallbackUsed ? Strings.Msg_TextFallbackApp : null);
        }
        catch (Exception) { return new ObjectToolResult(Strings.Msg_ContentEditFailed, null); }
    }

    /// <summary>새 텍스트(앱 글꼴 한 줄)를 클릭한 점(글자 상자의 왼쪽 위)에 넣는다. 빈 글이면 아무것도 하지 않는다.</summary>
    public static async Task<ObjectToolResult> AddTextAsync(DocumentTabViewModel tab, PageId page, PdfPointF clickTopLeft, int pageRotationQuarter,
        string text, float sizePt = DefaultTextSizePt)
    {
        if (tab.Session is not { } s || !tab.CanEditContent || string.IsNullOrWhiteSpace(text)) return ObjectToolResult.None;
        try
        {
            var font = DocumentTabViewModel.Appearance?.Font;
            var placement = TextPlacement(clickTopLeft, sizePt, pageRotationQuarter);
            var plan = await s.Thread.InvokeAsync(() => TextEditPlanner.PlanAdd(s, text, sizePt, new PdfColor(0, 0, 0), font, placement));
            if (plan.Edits.Count == 0) return new ObjectToolResult(RefusalMessage(plan), null);
            await tab.RunContentCommandAsync(new ContentEditCommand(page, plan.Edits));
            var line = (InsertAppTextEdit)plan.Edits[0];
            return new ObjectToolResult(null, null, ObjectRef.Inserted(line.Id), line.Line.Bounds, PdfPageObjectType.Path);
        }
        catch (Exception) { return new ObjectToolResult(Strings.Msg_ContentEditFailed, null); }
    }

    /// <summary>이미지(PNG·JPEG)를 쪽 가운데에 쪽 폭의 절반 안으로 넣는다. 형식·크기 검사는 문서 자산 보관소가 한다.</summary>
    public static async Task<ObjectToolResult> AddImageAsync(DocumentTabViewModel tab, PageId page, byte[] encoded)
    {
        if (tab.Session is not { } s || !tab.CanEditContent) return ObjectToolResult.None;
        try
        {
            string asset = s.Assets.AddImage(encoded);   // 형식·크기가 안 맞으면 예외
            var (_, w, h, _) = s.Assets.GetImage(asset);
            var (bounds, quarter) = await s.Thread.InvokeAsync(() =>
            {
                var p = s.GetLoadedPage(page);
                return (p.BoundsPt, p.Rotation);
            });
            var placement = ImagePlacement(PdfRectF.FromLtrbRectangle(bounds), quarter, w, h);
            var edit = new InsertImageEdit(s.Content.NewId(), asset, placement);
            await tab.RunContentCommandAsync(new ContentEditCommand(page, [edit]));
            return new ObjectToolResult(null, null, ObjectRef.Inserted(edit.Id), BoundsOf(placement), PdfPageObjectType.Image);
        }
        catch (Exception) { return new ObjectToolResult(Strings.Msg_ImageInsertFailed, null); }
    }

    /// <summary>화면에서 바로 선 이미지가 쪽 가운데에 오게 하는 행렬(단위 사각형 → 쪽 좌표). 보이는 폭의 50 % 이내, 높이는 80 % 이내.</summary>
    public static PdfMatrix ImagePlacement(PdfRectF pageBounds, int pageRotationQuarter, int pixelsW, int pixelsH)
    {
        bool swap = pageRotationQuarter % 2 == 1;
        float viewW = swap ? pageBounds.Height : pageBounds.Width, viewH = swap ? pageBounds.Width : pageBounds.Height;
        float k = Math.Min(viewW * 0.5f / Math.Max(1, pixelsW), viewH * 0.8f / Math.Max(1, pixelsH));
        float w = Math.Max(1f, pixelsW * k), h = Math.Max(1f, pixelsH * k);
        var c = new PdfPointF((pageBounds.Left + pageBounds.Right) / 2f, (pageBounds.Bottom + pageBounds.Top) / 2f);
        return new PdfMatrix(w, 0, 0, h, c.X - w / 2f, c.Y - h / 2f).Then(AnnotationRotation.About(c, pageRotationQuarter));
    }

    public static PdfRectF BoundsOf(PdfMatrix m) =>
        PdfRectF.FromPoints([m.Apply(new(0, 0)), m.Apply(new(1, 0)), m.Apply(new(0, 1)), m.Apply(new(1, 1))]);

    /// <summary>너무 작으면(사각형·타원은 어느 한 변이, 선은 길이가 이보다 작으면) 만들지 않는다.</summary>
    public const float MinShapePt = 3f;

    public static ShapeSpec? MakeShape(ShapeKind kind, PdfPointF a, PdfPointF b)
    {
        float w = Math.Abs(b.X - a.X), h = Math.Abs(b.Y - a.Y);
        if (kind == ShapeKind.Line ? Math.Sqrt(w * w + h * h) < MinShapePt : w < MinShapePt || h < MinShapePt) return null;
        var r = new PdfRectF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        return new ShapeSpec(kind, r, a, b, ShapeStyle.Default);
    }

    public static async Task<ObjectToolResult> AddShapeAsync(DocumentTabViewModel tab, PageId page, ShapeKind kind, PdfPointF a, PdfPointF b)
    {
        if (tab.Session is null || !tab.CanEditContent || MakeShape(kind, a, b) is not { } spec) return ObjectToolResult.None;
        try
        {
            var edit = new InsertShapeEdit(tab.Session.Content.NewId(), spec);
            await tab.RunContentCommandAsync(new ContentEditCommand(page, [edit]));
            return new ObjectToolResult(null, null, ObjectRef.Inserted(edit.Id), spec.Bounds, PdfPageObjectType.Path);
        }
        catch (Exception) { return new ObjectToolResult(Strings.Msg_ContentEditFailed, null); }
    }
}
