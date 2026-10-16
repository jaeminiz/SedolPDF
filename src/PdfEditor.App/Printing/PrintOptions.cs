namespace PdfEditor.App.Printing;

public enum PrintScaling { FitToPaper, ActualSize }

/// <summary>인쇄 옵션 대화상자에서 고른 값.</summary>
public sealed record PrintOptions(bool WithAnnotations = true, PrintScaling Scaling = PrintScaling.FitToPaper, bool AutoRotate = true);
