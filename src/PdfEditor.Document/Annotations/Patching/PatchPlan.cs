using System.Collections.Immutable;
using Pdf.Native;

namespace PdfEditor.Document.Annotations.Patching;

/// <summary>임시 파일의 기존 주석을 그 자리에서 고친다. /NM이 Nm과 다르거나, Subtype이 있는데 /Subtype이 다르면 InvalidOperationException.</summary>
public sealed record AnnotationPatch(int PageIndex, int AnnotIndex, string Nm, ImmutableArray<PatchOp> Ops, string? Subtype = null);

/// <summary>새 주석 딕셔너리를 만들어 /Annots 끝에 붙인 뒤 Ops를 적용한다.</summary>
public sealed record AnnotationCreate(int PageIndex, string Subtype, string Nm, PdfRectF Rect, int Flags, ImmutableArray<PatchOp> Ops);

public sealed record AnnotationRemoval(int PageIndex, ImmutableArray<int> AnnotIndices);

/// <summary>
/// 쪽 내용 스트림 끝에 새 스트림을 붙인다(q … Q로 감쌈, M4 C6·C7). Content의 "{F}"는 쪽 /Resources /Font에 새로 넣는
/// 글꼴(Font로 서브셋) 자원 이름으로 바뀐다(이름이 겹치지 않게 적용기가 고름). 매 출력마다 사본에만(상태 없음).
/// </summary>
public sealed record PageContentAppend(int PageIndex, string Content, FontUse Font);

/// <summary>PDFium이 알려 준 원본 객체 하나(종류 = PdfPageObjectType 값, 쪽 공간 경계). 해석기 객체 목록과 대응을 검증하는 데 쓴다.</summary>
public readonly record struct ReferenceObj(int Kind, double L, double B, double R, double T);

/// <summary>
/// 이 쪽의 객체 편집을 PDFium GenerateContent 대신 내용 스트림 직접 고쳐 쓰기로 확정한다(M4x). Edits = 그 쪽의 편집 기록 전체(적용 순서),
/// Reference = 편집 전 원본 쪽의 PDFium 객체 목록. 해석기 객체 목록과 어긋나면 적용기는 ContentRegenerationException을 던진다.
/// </summary>
public sealed record PageContentRewrite(int PageIndex, ImmutableArray<Content.ContentEdit> Edits, ImmutableArray<ReferenceObj> Reference);

/// <summary>
/// 저장 파일에만 적용할 변경 목록. 매번 모델에서 새로 만든다(상태 없음) — 같은 계획을 같은 SaveCopy 결과에
/// 여러 번 적용해도 결과가 같다.
/// </summary>
public sealed record PatchPlan(
    ImmutableArray<AnnotationPatch> Patches,
    ImmutableArray<AnnotationCreate> Creates,
    ImmutableArray<AnnotationRemoval> Removals)
{
    public static PatchPlan Empty { get; } = new([], [], []);

    /// <summary>쪽 내용 스트림 끝에 덧붙일 것(M4 앱 글꼴 줄). 주석 수에는 영향 없음.</summary>
    public ImmutableArray<PageContentAppend> ContentAppends { get; init; } = [];

    /// <summary>PDFium이 못 다시 쓰는 쪽의 객체 편집을 내용 스트림 고쳐 쓰기로 확정할 쪽들(M4x). OutputCopyBuilder가 채운다 — 이 쪽들의 ContentAppends는 없어야 한다(고쳐 쓰기가 함께 한다).</summary>
    public ImmutableArray<PageContentRewrite> ContentRewrites { get; init; } = [];

    public bool IsEmpty => Patches.IsDefaultOrEmpty && Creates.IsDefaultOrEmpty && Removals.IsDefaultOrEmpty && ContentAppends.IsDefaultOrEmpty && ContentRewrites.IsDefaultOrEmpty;

    /// <summary>그 쪽의 주석 수 변화: 만들기 + Popup(EnsurePopup마다 1) − 지우기.</summary>
    public int CountDelta(int pageIndex)
    {
        int delta = 0;
        if (!Creates.IsDefaultOrEmpty)
            foreach (var c in Creates)
                if (c.PageIndex == pageIndex)
                {
                    delta++;
                    delta += CountPopups(c.Ops);
                }
        if (!Patches.IsDefaultOrEmpty)
            foreach (var p in Patches)
                if (p.PageIndex == pageIndex) delta += CountPopups(p.Ops);
        if (!Removals.IsDefaultOrEmpty)
            foreach (var r in Removals)
                if (r.PageIndex == pageIndex) delta -= r.AnnotIndices.Distinct().Count();   // 적용기도 중복 번호는 한 번만 지운다
        return delta;
    }

    private static int CountPopups(ImmutableArray<PatchOp> ops)
    {
        if (ops.IsDefaultOrEmpty) return 0;
        int n = 0;
        foreach (var op in ops) if (op is EnsurePopup) n++;
        return n;
    }
}
