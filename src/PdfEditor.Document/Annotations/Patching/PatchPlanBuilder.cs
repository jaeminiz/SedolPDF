using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Saving;

namespace PdfEditor.Document.Annotations.Patching;

/// <summary>
/// 모델 전체에서 저장 파일에 적용할 패치 계획을 새로 만든다(상태 없음, A1). Flush 직후, PDFium 전용 스레드에서 부른다.
/// 쪽 번호 = 현재 쪽 인덱스, 주석 번호 = Flush 뒤 PdfIndex(= SaveCopy 파일의 /Annots 번호).
/// </summary>
public static class PatchPlanBuilder
{
    public static PatchPlan Build(DocumentSession s)
    {
        s.Thread.AssertOnThread();
        if (!SavePolicy.CanPatch(s)) return PatchPlan.Empty;

        var patches = ImmutableArray.CreateBuilder<AnnotationPatch>();
        var removals = ImmutableArray.CreateBuilder<AnnotationRemoval>();

        foreach (var (pageId, entries) in s.Annotations.AllPages())
        {
            int pageIndex = s.IndexOf(pageId);
            if (pageIndex < 0) continue;   // 문서에 없는(삭제되어 UndoStash에 있는) 쪽
            var page = s.GetLoadedPage(pageId);
            int n0 = page.AnnotationCount;

            foreach (var e in entries)
            {
                if (e.Deleted || e.PdfIndex is not int idx || idx < 0 || idx >= n0) continue;
                var ops = ImmutableArray.CreateBuilder<PatchOp>();

                if (!e.IsOriginal && e.Current.Kind == AnnotKind.Text)
                {
                    // ① 새 메모: Popup이 아직 없을 때만(CountDelta가 EnsurePopup마다 +1로 센다).
                    if (page.GetLinkedAnnotationIndex(idx, "Popup") is null)
                    {
                        var r = e.Current.Rect;
                        ops.Add(new EnsurePopup(new PdfRectF(r.Right + 4, r.Top - 120, r.Right + 4 + 180, r.Top), Open: false));
                    }
                }
                else if (e.IsOriginal && e.Owned && e.Current.Kind is AnnotKind.Ink or AnnotKind.Square or AnnotKind.Circle)
                {
                    // ② 손댄 원본: 두께는 /Border에 썼으므로 낡은 /BS를 지우고, 채우기를 없앴으면 /IC도 지운다.
                    ops.Add(new RemoveKeys(["/BS"]));
                    if (e.Current.Kind is AnnotKind.Square or AnnotKind.Circle && e.Current.FillColor is null)
                        ops.Add(new RemoveKeys(["/IC"]));
                }
                if (ops.Count == 0) continue;

                using var a = page.GetAnnotation(idx);
                string nm = a.GetString("NM") ?? "";
                // /NM이 없는 주석은 빈 문자열과 비교되어 보호가 약하므로 하위 유형도 함께 확인한다.
                patches.Add(new AnnotationPatch(pageIndex, idx, nm, ops.ToImmutable(), e.Current.Kind.ToString()));
            }

            // ③ 삭제된 원본(A4): PDFium에는 그대로 있고 저장 파일에서만 지운다. 번호는 Flush와 같은 규칙으로 계산.
            var kept = entries.Where(e => e.Deleted && e.IsOriginal && e.PdfIndex is not null).ToList();
            if (kept.Count == 0) continue;
            var survivorIdx = new HashSet<int>(entries.Where(e => !e.Deleted && e.PdfIndex is not null).Select(e => e.PdfIndex!.Value));
            var remove = AnnotationFlusher.ComputeRemoval(page, n0, kept, survivorIdx);
            removals.Add(new AnnotationRemoval(pageIndex, [.. remove.Reverse()]));
        }

        if (patches.Count == 0 && removals.Count == 0) return PatchPlan.Empty;
        return new PatchPlan(patches.ToImmutable(), [], removals.ToImmutable());
    }
}
