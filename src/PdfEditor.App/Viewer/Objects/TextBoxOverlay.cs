using Pdf.Native;
using PdfEditor.Document.Content;

namespace PdfEditor.App.Viewer.Objects;

/// <summary>
/// 텍스트 편집 도구를 켰을 때 쪽 위에 미리 보이는 "고칠 수 있는 글 조각" 상자의 계산(Avalonia 없음).
/// 가짜 굵게처럼 같은 자리에 겹쳐 찍힌 글 객체는 상자 하나로 합친다.
/// </summary>
public static class TextBoxOverlay
{
    /// <summary>이보다 작은 상자(글 없는 빈 글 객체 등)는 그리지 않는다(pt).</summary>
    public const float MinSizePt = 1f;

    /// <summary>글 객체(그리고 앱 글꼴로 넣어 화면에서는 경로인 줄)의 경계를 겹침 없이 모은다.</summary>
    public static IReadOnlyList<PdfRectF> Boxes(PageObjectIndex idx, ISet<long> appTextIds)
    {
        var result = new List<PdfRectF>();
        foreach (var e in idx.Entries)
        {
            bool isText = e.Type == PdfPageObjectType.Text || (e.Ref.IsInserted && appTextIds.Contains(e.Ref.InsertedId));
            if (!isText || e.Bounds.Width < MinSizePt || e.Bounds.Height < MinSizePt) continue;
            float tol = Math.Max(0.5f, 0.15f * e.Bounds.Height);
            if (result.Any(r => Near(r, e.Bounds, tol))) continue;
            result.Add(e.Bounds);
        }
        return result;
    }

    /// <summary>같은 자리(경계가 거의 같음)에 겹쳐 찍힌 다른 원본 글 객체가 있나(가짜 굵게).</summary>
    public static bool HasOverlappedCopy(PageObjectIndex idx, ObjectRef target, PdfRectF bounds) => CopiesOf(idx, target, bounds).Count > 0;

    /// <summary>같은 자리에 겹쳐 찍힌 다른 원본 글 객체들(가짜 굵게). 색·크기·위치를 바꿀 때 함께 바꾼다.</summary>
    public static IReadOnlyList<ObjectRef> CopiesOf(PageObjectIndex idx, ObjectRef target, PdfRectF bounds)
    {
        float tol = Math.Max(0.5f, 0.15f * bounds.Height);
        return [.. idx.Entries.Where(e => e.Type == PdfPageObjectType.Text && !e.Ref.IsInserted && e.Ref != target && Near(e.Bounds, bounds, tol)).Select(e => e.Ref)];
    }

    /// <summary>점 p(쪽 좌표)를 덮는 상자. 여럿이면 가장 작은 것.</summary>
    public static PdfRectF? HoverBox(IReadOnlyList<PdfRectF> boxes, PdfPointF p, float tolerancePt)
    {
        PdfRectF? best = null;
        foreach (var b in boxes)
            if (b.Inflate(tolerancePt).Contains(p) && (best is null || b.Width * b.Height < best.Value.Width * best.Value.Height)) best = b;
        return best;
    }

    private static bool Near(PdfRectF a, PdfRectF b, float tol)
        => Math.Abs(a.Left - b.Left) <= tol && Math.Abs(a.Right - b.Right) <= tol && Math.Abs(a.Bottom - b.Bottom) <= tol && Math.Abs(a.Top - b.Top) <= tol;
}
