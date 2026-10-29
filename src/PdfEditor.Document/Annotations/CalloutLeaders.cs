using System.Collections.Immutable;
using Pdf.Native;

namespace PdfEditor.Document.Annotations;

/// <summary>
/// 설명선의 지시선 2개 규칙(0.4.0 Task 3). 첫 지시선은 <see cref="Annotation.Vertices"/>(표준 /CL), 둘째는 <see cref="Annotation.Leader2"/>(/SedolCL2).
/// 점은 모두 "끝이 가리키는 점부터, 마지막이 글 상자에 닿는 점"이다. 순수 함수라 스레드와 무관하다.
/// </summary>
public static class CalloutLeaders
{
    public const int MaxLeaders = 2;
    /// <summary>둘째 지시선의 기본 길이(상자 변에서 끝점까지, pt).</summary>
    public const float DefaultLengthPt = 40f;

    public static bool IsCallout(Annotation a)
        => a is { Kind: AnnotKind.FreeText, Intent: FreeTextIntent.Callout, TextBox: not null } && !a.Vertices.IsDefaultOrEmpty && a.Vertices.Length >= 2;

    public static int Count(Annotation a) => !IsCallout(a) ? 0 : !a.Leader2.IsDefaultOrEmpty && a.Leader2.Length >= 2 ? 2 : 1;

    public static bool CanAdd(Annotation a) => IsCallout(a) && Count(a) < MaxLeaders;

    private enum Side { Left, Right, Top, Bottom }

    private static Side SideOf(PdfRectF box, PdfPointF p)
    {
        float dl = Math.Abs(p.X - box.Left), dr = Math.Abs(p.X - box.Right), dt = Math.Abs(p.Y - box.Top), db = Math.Abs(p.Y - box.Bottom);
        float m = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
        return m == dl ? Side.Left : m == dr ? Side.Right : m == dt ? Side.Top : Side.Bottom;
    }

    /// <summary>
    /// 둘째 지시선을 더한다: 첫 지시선이 닿은 변의 반대쪽 변 가운데 → 꺾임점(변에서 수직으로 반 길이) → 끝점(비스듬히 반 길이 더).
    /// 두 지시선이 겹치지 않는다. bounds가 있으면 꺾임점·끝점을 그 안으로 모은다. 더할 수 없으면(설명선이 아님·이미 2개) a 그대로.
    /// Rect는 호출자가 <see cref="AnnotationGeometry.ComputeRect"/>로 다시 계산한 결과를 돌려준다.
    /// </summary>
    public static Annotation AddSecond(Annotation a, PdfRectF? bounds = null, float lengthPt = DefaultLengthPt)
    {
        if (!CanAdd(a)) return a;
        var box = a.TextBox!.Value;
        var side = SideOf(box, a.Vertices[^1]) switch { Side.Left => Side.Right, Side.Right => Side.Left, Side.Top => Side.Bottom, _ => Side.Top };
        float cx = (box.Left + box.Right) / 2f, cy = (box.Bottom + box.Top) / 2f;
        // (nx, ny) = 변의 바깥 방향, (tx, ty) = 변을 따라 움직이는 방향(끝점을 옆으로 비껴 꺾임이 보이게)
        (PdfPointF attach, float nx, float ny, float tx, float ty) = side switch
        {
            Side.Left => (new PdfPointF(box.Left, cy), -1f, 0f, 0f, 1f),
            Side.Right => (new PdfPointF(box.Right, cy), 1f, 0f, 0f, 1f),
            Side.Top => (new PdfPointF(cx, box.Top), 0f, 1f, 1f, 0f),
            _ => (new PdfPointF(cx, box.Bottom), 0f, -1f, 1f, 0f),
        };
        float half = lengthPt / 2f;
        var elbow = new PdfPointF(attach.X + nx * half, attach.Y + ny * half);
        var tip = new PdfPointF(elbow.X + (nx + tx) * half, elbow.Y + (ny + ty) * half);
        if (bounds is { } b)
        {
            elbow = Clamp(elbow, b);
            tip = Clamp(tip, b);
        }
        var b2 = a with { Leader2 = [tip, elbow, attach] };
        return b2 with { Rect = AnnotationGeometry.ComputeRect(b2) };
    }

    /// <summary>둘째 지시선을 지운다. 없으면 a 그대로.</summary>
    public static Annotation RemoveSecond(Annotation a)
    {
        if (Count(a) < 2 && (a.Leader2.IsDefaultOrEmpty || a.Leader2.Length == 0)) return a;
        var b = a with { Leader2 = [] };
        return b with { Rect = AnnotationGeometry.ComputeRect(b) };
    }

    private static PdfPointF Clamp(PdfPointF p, PdfRectF r)
        => new(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Bottom, r.Top));
}
