using System.Collections.Immutable;
using Pdf.Native;

namespace PdfEditor.Document.Annotations.Patching;

/// <summary>임시 파일의 기존 주석을 그 자리에서 고친다. /NM이 Nm과 다르면 InvalidOperationException.</summary>
public sealed record AnnotationPatch(int PageIndex, int AnnotIndex, string Nm, ImmutableArray<PatchOp> Ops);

/// <summary>새 주석 딕셔너리를 만들어 /Annots 끝에 붙인 뒤 Ops를 적용한다.</summary>
public sealed record AnnotationCreate(int PageIndex, string Subtype, string Nm, PdfRectF Rect, int Flags, ImmutableArray<PatchOp> Ops);

public sealed record AnnotationRemoval(int PageIndex, ImmutableArray<int> AnnotIndices);

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

    public bool IsEmpty => Patches.IsDefaultOrEmpty && Creates.IsDefaultOrEmpty && Removals.IsDefaultOrEmpty;

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
                if (r.PageIndex == pageIndex) delta -= r.AnnotIndices.Length;
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
