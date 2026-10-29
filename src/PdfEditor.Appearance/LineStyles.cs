using System.Collections.Immutable;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

/// <summary>선 모양의 그리기 값(0.4.0 D1). 계산은 Document의 <see cref="LineStyleExtensions"/> 한 곳에 있고 Painter들이 이를 거쳐 쓴다.</summary>
public static class LineStyles
{
    /// <summary>대시 배열(굵기 비례). Solid는 빈 배열.</summary>
    public static float[] DashArray(LineStyle s, float width) => LineStyleExtensions.DashArray(s, width);

    internal static ImmutableArray<float> Dash(LineStyle s, float width) => s == LineStyle.Solid ? default : [.. DashArray(s, width)];

    internal static LineCap CapFor(LineStyle s) => s.UsesRoundCap() ? LineCap.Round : LineCap.Butt;
}
