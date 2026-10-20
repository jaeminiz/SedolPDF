using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;

namespace PdfEditor.TextEdit;

/// <summary>바꾸기를 할 수 없는 이유(화면 문구는 앱이 리소스에서 고른다).</summary>
public enum ReplaceRefusal { None, NotText, Encrypted, NoAppFont, AppFontMissingGlyphs }

/// <summary>텍스트 한 줄 바꾸기의 결과. Edits가 비어 있지 않으면 ContentEditCommand 하나로 실행한다.</summary>
public sealed record ReplacePlan(IReadOnlyList<ContentEdit> Edits, bool FallbackUsed, ReplaceRefusal Refusal, IReadOnlyList<int> MissingCodepoints)
{
    public static ReplacePlan Refused(ReplaceRefusal r, IReadOnlyList<int>? missing = null) => new([], false, r, missing ?? []);
}

/// <summary>
/// 텍스트 한 줄(객체 하나) 바꾸기 계획(M4 결정 C4). PDFium 전용 스레드에서 부른다.
/// ① 원래 글꼴로 바꿔 V14 규칙을 통과하면 원래 글꼴 ② 아니면(패치 가능하고 앱 글꼴이 있으면) 그 줄 전체를 앱 글꼴로
/// ③ 그것도 안 되면 거절 이유. 빈 글은 그 줄 지우기.
/// </summary>
public static class TextEditPlanner
{
    public static ReplacePlan PlanReplace(DocumentSession s, PageId page, ObjectRef target, string newText, AppFont? appFont,
        float? sizeOverride = null, PdfColor? colorOverride = null)
    {
        s.Thread.AssertOnThread();
        int pageIndex = s.IndexOf(page);
        if (pageIndex < 0) return ReplacePlan.Refused(ReplaceRefusal.NotText);
        var st = s.Content;
        var edits = st.For(page);

        if (string.IsNullOrWhiteSpace(newText))
            return new ReplacePlan([new DeactivateEdit(st.NewId(), target)], false, ReplaceRefusal.None, []);

        // 바꿀 줄의 모양(위치 행렬·크기·색)과 원래 글꼴의 출처(원본 텍스트 객체 번호)
        var r = s.GetReplay(page);
        int? sourceOriginal = null;
        PdfMatrix placement;
        float size;
        PdfColor color;
        var app = target.IsInserted ? edits.OfType<InsertAppTextEdit>().FirstOrDefault(a => a.Id == target.InsertedId) : null;
        if (app is not null)
        {
            // 앱 글꼴 줄을 다시 고침: 같은 자리·크기·색으로 새 앱 글꼴 줄 (그 뒤에 옮긴 것은 화면의 경로 객체에 반영돼 있다)
            placement = app.Line.Placement;
            foreach (var t in edits.OfType<TransformEdit>().Where(t => t.Target == target)) placement = placement.Then(t.Then);
            size = sizeOverride ?? app.Line.SizePt;
            color = colorOverride ?? app.Line.Color;
        }
        else
        {
            if (r.Resolve(target) is not { Type: PdfPageObjectType.Text } obj) return ReplacePlan.Refused(ReplaceRefusal.NotText);
            sourceOriginal = target.IsInserted
                ? edits.OfType<InsertTextLikeEdit>().FirstOrDefault(e => e.Id == target.InsertedId)?.Original.OriginalIndex
                : target.OriginalIndex;
            placement = obj.Matrix;
            size = obj.FontSize;
            var fc = obj.FillColor;
            color = fc is { } c ? new PdfColor(c.R, c.G, c.B) : new PdfColor(0, 0, 0);
        }

        // ① 원래 글꼴
        if (sourceOriginal is int src && src >= 0 && OriginalFontReplacer.Verify(s, pageIndex, src, newText) == OriginalFontReplacer.Result.Ok)
        {
            var insert = new InsertTextLikeEdit(st.NewId(), ObjectRef.Original(src), newText);
            // 새 줄은 replay 때 원본 객체의 "그 시점" 행렬을 복사하므로 원본을 옮긴 것은 이미 들어 있다.
            // 대상이 앞서 바꿔 넣은 줄이면, 그 줄에만 한 옮기기를 새 줄에도 덧붙인다.
            var moves = target.IsInserted
                ? edits.OfType<TransformEdit>().Where(t => t.Target == target).Select(t => (ContentEdit)new TransformEdit(st.NewId(), ObjectRef.Inserted(insert.Id), t.Then)).ToList()
                : [];
            return new ReplacePlan([insert, .. moves, new DeactivateEdit(st.NewId(), target)], false, ReplaceRefusal.None, []);
        }

        // ② 앱 글꼴
        if (!SavePolicy.CanPatch(s)) return ReplacePlan.Refused(ReplaceRefusal.Encrypted);
        if (appFont is null) return ReplacePlan.Refused(ReplaceRefusal.NoAppFont);
        var (spec, missing) = AppFontLine.Build(appFont, newText, size, placement, color);
        if (spec is null) return ReplacePlan.Refused(ReplaceRefusal.AppFontMissingGlyphs, missing);
        return new ReplacePlan([new InsertAppTextEdit(st.NewId(), spec), new DeactivateEdit(st.NewId(), target)], true, ReplaceRefusal.None, []);
    }

    /// <summary>새 텍스트(앱 글꼴 한 줄)를 baseline 왼쪽 점 at에 넣는 편집. 암호 문서·글꼴 없음·없는 글자면 거절.</summary>
    public static ReplacePlan PlanAdd(DocumentSession s, PdfPointF at, string text, float sizePt, PdfColor color, AppFont? appFont)
        => PlanAdd(s, text, sizePt, color, appFont, PdfMatrix.Translate(at.X, at.Y));

    /// <summary>placement: 기준선 왼쪽 점이 원점인 글 → 쪽 좌표 행렬(회전된 쪽에서 글이 바로 서게 돌린 것).</summary>
    public static ReplacePlan PlanAdd(DocumentSession s, string text, float sizePt, PdfColor color, AppFont? appFont, PdfMatrix placement)
    {
        s.Thread.AssertOnThread();
        if (string.IsNullOrWhiteSpace(text)) return ReplacePlan.Refused(ReplaceRefusal.NotText);
        if (!SavePolicy.CanPatch(s)) return ReplacePlan.Refused(ReplaceRefusal.Encrypted);
        if (appFont is null) return ReplacePlan.Refused(ReplaceRefusal.NoAppFont);
        var (spec, missing) = AppFontLine.Build(appFont, text, sizePt, placement, color);
        if (spec is null) return ReplacePlan.Refused(ReplaceRefusal.AppFontMissingGlyphs, missing);
        return new ReplacePlan([new InsertAppTextEdit(s.Content.NewId(), spec)], true, ReplaceRefusal.None, []);
    }
}
