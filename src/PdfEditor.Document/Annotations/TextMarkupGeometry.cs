using Pdf.Native;

namespace PdfEditor.Document.Annotations;

/// <summary>
/// 텍스트 선택 → 강조·밑줄·취소선·바꾸기 QuadPoints. 글자(또는 낱말) 상자를 같은 줄끼리 하나로 이어 붙여 줄마다 사각형 하나로 만든다
/// (낱말 사이 공백·글자 사이 틈 포함, 높이는 그 줄 상자의 위아래 최대 범위). 순수 함수라 스레드와 무관하다.
/// </summary>
public static class TextMarkupGeometry
{
    private const float MinOverlap = 0.5f;   // 같은 줄로 보는 가로줄(세로줄) 방향 겹침: 작은 쪽 두께의 절반 이상
    private const float MaxGapEm = 3f;       // 이어 붙일 최대 틈: 두께의 3배(낱말 사이·양끝 맞춤 공백·점선 안내선까지)
    private const float AxisRatio = 3f;      // 이동량이 한 방향으로 이 비율 이상 치우쳐야 가로·세로쓰기로 본다

    /// <summary>읽는 순서의 글자 상자들을 줄별 사각형으로 합친다. 대각선처럼 가로도 세로도 아닌 글줄은 손대지 않고 그대로 돌려준다.</summary>
    public static IReadOnlyList<PdfRectF> MergeLines(IReadOnlyList<PdfRectF> rects)
    {
        var src = rects.Where(r => r.Width > 0f && r.Height > 0f).ToList();
        if (src.Count <= 1) return src.Count == rects.Count ? rects : src;

        // 글줄 방향: 이웃한 상자 중심의 가로·세로 이동량 합을 견준다.
        float dx = 0, dy = 0;
        for (int i = 1; i < src.Count; i++)
        {
            dx += MathF.Abs(Cx(src[i]) - Cx(src[i - 1]));
            dy += MathF.Abs(Cy(src[i]) - Cy(src[i - 1]));
        }
        bool horizontal = dy * AxisRatio < dx, vertical = dx * AxisRatio < dy;
        if (!horizontal && !vertical) return src;

        var result = new List<PdfRectF>();
        var cur = src[0];
        for (int i = 1; i < src.Count; i++)
        {
            if (SameLine(cur, src[i], horizontal)) cur = cur.Union(src[i]);
            else { result.Add(cur); cur = src[i]; }
        }
        result.Add(cur);
        return result;
    }

    public static IReadOnlyList<PdfQuadF> ToQuads(IReadOnlyList<PdfRectF> rects) => [.. MergeLines(rects).Select(PdfQuadF.FromRect)];

    private static float Cx(PdfRectF r) => (r.Left + r.Right) / 2f;
    private static float Cy(PdfRectF r) => (r.Bottom + r.Top) / 2f;

    private static bool SameLine(PdfRectF a, PdfRectF b, bool horizontal)
    {
        // 가로쓰기: 세로로 겹치고 가로 틈이 작다. 세로쓰기는 축을 바꿔 같은 규칙.
        (float aLo, float aHi, float bLo, float bHi) cross = horizontal ? (a.Bottom, a.Top, b.Bottom, b.Top) : (a.Left, a.Right, b.Left, b.Right);
        (float aLo, float aHi, float bLo, float bHi) along = horizontal ? (a.Left, a.Right, b.Left, b.Right) : (a.Bottom, a.Top, b.Bottom, b.Top);
        float crossOverlap = MathF.Min(cross.aHi, cross.bHi) - MathF.Max(cross.aLo, cross.bLo);
        float crossMin = MathF.Min(cross.aHi - cross.aLo, cross.bHi - cross.bLo);
        if (crossOverlap < crossMin * MinOverlap) return false;
        float gap = MathF.Max(along.bLo - along.aHi, along.aLo - along.bHi);   // 겹치면 0 이하
        return gap <= MaxGapEm * MathF.Max(cross.aHi - cross.aLo, cross.bHi - cross.bLo);
    }
}
