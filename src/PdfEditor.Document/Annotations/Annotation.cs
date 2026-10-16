using System.Collections.Immutable;
using Pdf.Native;

namespace PdfEditor.Document.Annotations;

/// <summary>
/// 문서 안의 주석 하나(불변). 바꿀 때는 <c>with</c>를 쓴다.
/// Color: 선·마크업·아이콘 색 (Square·Circle은 null이면 선 없음). FillColor: Square·Circle 채우기 (null = 없음).
/// </summary>
public sealed record Annotation
{
    public AnnotId Id { get; init; }
    public AnnotKind Kind { get; init; }
    public PdfRectF Rect { get; init; }
    public PdfColor? Color { get; init; }
    public PdfColor? FillColor { get; init; }
    public float Opacity { get; init; } = 1f;
    public float BorderWidth { get; init; } = 1f;
    public ImmutableArray<ImmutableArray<PdfPointF>> InkStrokes { get; init; } = [];
    public ImmutableArray<PdfQuadF> Quads { get; init; } = [];
    public string? Contents { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    /// <summary>PDF /NM (고유 이름).</summary>
    public string? Name { get; init; }
    public int Flags { get; init; }
}
