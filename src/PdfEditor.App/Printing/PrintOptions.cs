namespace PdfEditor.App.Printing;

public enum PrintScaling { FitToPaper, ActualSize, Percent }
public enum PrintRangeMode { All, Current, Selected, Custom }
public enum PrintParity { All, Odd, Even }
public enum PrintOrientation { Auto, Portrait, Landscape }
public enum PrintDuplex { Off, LongEdge, ShortEdge }

/// <summary>
/// 인쇄 대화상자에서 고른 값. 앞의 세 값은 0.4.0 대화상자와 같은 자리(시험 호환).
/// PrinterName이 null이면 기본 프린터. PaperSize는 DEVMODE 용지 번호(DMPAPER_*), 0 = 프린터 기본.
/// ScalePercent는 Scaling이 Percent일 때만 쓴다(10~400). PagesPerSheet는 1·2·4.
/// 쪽 모아 찍기(2·4)에서는 배율을 항상 칸에 맞춤으로 본다.
/// </summary>
public sealed record PrintOptions(
    bool WithAnnotations = true,
    PrintScaling Scaling = PrintScaling.FitToPaper,
    bool AutoRotate = true,
    PrintRangeMode Range = PrintRangeMode.All,
    string RangeText = "",
    PrintParity Parity = PrintParity.All,
    string? PrinterName = null,
    int Copies = 1,
    bool Collate = true,
    PrintDuplex Duplex = PrintDuplex.Off,
    PrintOrientation Orientation = PrintOrientation.Auto,
    short PaperSize = 0,
    int ScalePercent = 100,
    bool Center = true,
    int PagesPerSheet = 1,
    bool Grayscale = false)
{
    public const int MaxCopies = 999;
    public const int MinPercent = 10, MaxPercent = 400;

    /// <summary>범위를 벗어난 값을 허용 범위로 맞춘다(저장된 옛 설정·손으로 고친 파일 방어).</summary>
    public PrintOptions Normalized() => this with
    {
        Copies = Math.Clamp(Copies, 1, MaxCopies),
        ScalePercent = Math.Clamp(ScalePercent, MinPercent, MaxPercent),
        PagesPerSheet = PagesPerSheet is 2 or 4 ? PagesPerSheet : 1,
        RangeText = RangeText ?? "",
    };
}
