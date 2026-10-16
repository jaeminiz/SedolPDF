using PdfEditor.App.Documents;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Viewer.Annotations;

/// <summary>도구별 기본 스타일과 색 견본 (계획 D12).</summary>
public static class ToolDefaults
{
    private static readonly PdfColor Red = PdfColor.FromHex("#E5372D");
    private static readonly PdfColor Blue = PdfColor.FromHex("#005FB8");
    private static readonly PdfColor Yellow = PdfColor.FromHex("#FFD60A");

    public static IReadOnlyList<PdfColor> Swatches { get; } =
    [
        .. new[] { "#E5372D", "#005FB8", "#1F9D55", "#FFD60A", "#2B2B2E", "#000000", "#FFFFFF", "#FF8C00", "#8E44AD", "#00A3BF" }
            .Select(PdfColor.FromHex),
    ];

    public static ToolStyle For(ToolKind tool) => tool switch
    {
        ToolKind.Pencil => new(Red, null, 1f, 1.5f),
        ToolKind.HighlightText or ToolKind.HighlightArea => new(Yellow, null, 0.5f, 1f),
        ToolKind.Underline => new(Blue, null, 1f, 1f),
        ToolKind.StrikeOut => new(Red, null, 1f, 1f),
        ToolKind.StickyNote => new(Yellow, null, 1f, 1f),
        ToolKind.Rectangle or ToolKind.Ellipse => new(Red, null, 1f, 1.5f),
        _ => new(Red, null, 1f, 1f),
    };
}
