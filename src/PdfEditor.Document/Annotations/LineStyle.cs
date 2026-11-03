namespace PdfEditor.Document.Annotations;

/// <summary>선 모양(0.4.0 D1). PDF에는 표준 /BS &lt;&lt; /W /S /D /D [..] &gt;&gt;로 쓴다(Solid는 /BS 없이 /Border만 쓴다 — 기존 파일과 같다).</summary>
public enum LineStyle { Solid, Dotted, Dashed, DashDot }

public static class LineStyleExtensions
{
    /// <summary>선 모양을 적용할 수 있는 주석 유형(코드에 고정한 표). 사각형·타원·연필·글 상자는 이번에 넣지 않는다(PDFium 길이라 패치가 따로 필요, 0.5.0 후보).</summary>
    public static bool Applies(this AnnotKind k) => k is AnnotKind.Line or AnnotKind.PolyLine or AnnotKind.Polygon;

    /// <summary>/BS의 /S 이름. Solid는 S, 나머지는 D(대시).</summary>
    public static string ToBsName(this LineStyle s) => s == LineStyle.Solid ? "S" : "D";

    /// <summary>
    /// 대시 배열(PDF /D·내용 스트림 d와 같은 단위: 켜짐, 꺼짐, ...). 굵기에 비례하고(1pt 아래는 1pt로 본다) Solid는 빈 배열.
    /// 점선은 둥근 끝(<see cref="UsesRoundCap"/>)과 함께 점으로 보이게 켜짐을 아주 짧게 둔다. 화면·AP·/BS가 모두 이 한 곳을 쓴다(D1).
    /// </summary>
    public static float[] DashArray(LineStyle s, float width)
    {
        float w = Math.Max(width, 1f);
        return s switch
        {
            LineStyle.Dotted => [0.1f * w, 2.5f * w],
            LineStyle.Dashed => [4f * w, 3f * w],
            LineStyle.DashDot => [6f * w, 2.5f * w, w, 2.5f * w],
            _ => [],
        };
    }

    /// <summary>점선만 둥근 끝(켜짐이 거의 0이라 끝 모양이 점이 된다).</summary>
    public static bool UsesRoundCap(this LineStyle s) => s == LineStyle.Dotted;

    /// <summary>
    /// 파일의 /BS 대시를 우리 선 모양으로 인식한다: 굵기에 맞춘 우리 배열과 5 % 안에서 같을 때만. 다르면 null(편집하면 모양이 바뀌므로 읽기 전용으로 둔다).
    /// 다른 프로그램이 쓴 점선 배열도 굵기 비례가 아니면 인식하지 못한다.
    /// </summary>
    public static LineStyle? FromDash(IReadOnlyList<double> dash, float width)
    {
        foreach (var s in new[] { LineStyle.Dotted, LineStyle.Dashed, LineStyle.DashDot })
        {
            var d = DashArray(s, width);
            if (d.Length != dash.Count) continue;
            bool same = true;
            for (int i = 0; i < d.Length && same; i++) same = Math.Abs(d[i] - dash[i]) <= 0.05 * d[i] + 0.002;
            if (same) return s;
        }
        return null;
    }
}
