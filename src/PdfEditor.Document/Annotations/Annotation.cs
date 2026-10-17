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

    // --- M3 ---
    /// <summary>Line: 시작·끝 2점. PolyLine·Polygon: n점. Callout: 지시선 2~3점(끝이 가리키는 점부터).</summary>
    public ImmutableArray<PdfPointF> Vertices { get; init; } = [];
    public LineEnding StartEnding { get; init; }
    public LineEnding EndEnding { get; init; }
    /// <summary>클라우드 테두리 세기(0 = 없음, 1이 기본, A8).</summary>
    public float CloudIntensity { get; init; }
    public FreeTextIntent? Intent { get; init; }
    /// <summary>설명선의 글 상자(나머지 유형은 null).</summary>
    public PdfRectF? TextBox { get; init; }
    public float FontSizePt { get; init; } = 12f;
    public PdfColor? TextColor { get; init; }
    public TextAlign Align { get; init; }
    public AnnotId? InReplyTo { get; init; }
    public ReplyType? ReplyKind { get; init; }
    public StampSpec? Stamp { get; init; }
    public string? AttachmentId { get; init; }
    public string? AttachmentName { get; init; }
}
