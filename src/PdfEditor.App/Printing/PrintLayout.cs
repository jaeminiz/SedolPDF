namespace PdfEditor.App.Printing;

/// <summary>FPDF_RenderPage에 넘길 위치·크기(인쇄 가능 영역 왼쪽 위 기준 픽셀). Width/Height는 회전을 적용한 뒤의 크기.</summary>
public readonly record struct PrintPlacement(int X, int Y, int Width, int Height, int Rotate);

public static class PrintLayout
{
    /// <summary>쪽 모아 찍기 칸 수(열, 행). 2쪽은 용지 방향에 맞춰 긴 쪽을 둘로 가른다.</summary>
    public static (int Cols, int Rows) Grid(int pagesPerSheet, bool landscapeSheet) => pagesPerSheet switch
    {
        2 => landscapeSheet ? (2, 1) : (1, 2),
        4 => (2, 2),
        _ => (1, 1),
    };

    /// <summary>
    /// 용지 방향. 자동 = 첫 쪽의 방향을 따르되, 2쪽 모아 찍기는 쪽을 나란히 놓기 좋게 반대로(세로 쪽 → 가로 용지).
    /// </summary>
    public static bool IsLandscapeSheet(PrintOrientation orientation, int pagesPerSheet, float firstPageWPt, float firstPageHPt)
    {
        if (orientation == PrintOrientation.Portrait) return false;
        if (orientation == PrintOrientation.Landscape) return true;
        bool pageLandscape = firstPageWPt > firstPageHPt;
        return pagesPerSheet == 2 ? !pageLandscape : pageLandscape;
    }

    /// <summary>한 쪽을 영역(0,0)-(printableWPx,printableHPx) 안에 놓는다. 모아 찍기 칸에는 칸 크기를 영역으로 넘기고 결과에 칸 원점을 더한다.</summary>
    public static PrintPlacement Compute(float pageWPt, float pageHPt, int printableWPx, int printableHPx, int dpiX, int dpiY, PrintOptions o)
    {
        bool pageLandscape = pageWPt > pageHPt;
        bool areaLandscape = printableWPx > printableHPx;
        bool rotate = o.AutoRotate && pageLandscape != areaLandscape;
        double w = rotate ? pageHPt : pageWPt;
        double h = rotate ? pageWPt : pageHPt;

        // 회전 후 가로 방향이 장치 x축이다.
        double dw = w * dpiX / 72.0;
        double dh = h * dpiY / 72.0;
        var scaling = o.PagesPerSheet > 1 ? PrintScaling.FitToPaper : o.Scaling;
        if (scaling == PrintScaling.FitToPaper && dw > 0 && dh > 0)
        {
            double k = Math.Min(printableWPx / dw, printableHPx / dh);
            dw *= k;
            dh *= k;
        }
        else if (scaling == PrintScaling.Percent)
        {
            double k = Math.Clamp(o.ScalePercent, PrintOptions.MinPercent, PrintOptions.MaxPercent) / 100.0;
            dw *= k;
            dh *= k;
        }
        int width = Math.Max(1, (int)Math.Round(dw));
        int height = Math.Max(1, (int)Math.Round(dh));
        bool center = o.Center || o.PagesPerSheet > 1;
        int x = center ? (int)Math.Round((printableWPx - dw) / 2.0) : 0;
        int y = center ? (int)Math.Round((printableHPx - dh) / 2.0) : 0;
        return new PrintPlacement(x, y, width, height, rotate ? 1 : 0);
    }

    /// <summary>모아 찍기 칸 cell(0부터)의 위치. 인쇄 가능 영역을 균등 분할하고 남는 픽셀은 마지막 열·행이 먹는다.</summary>
    public static (int X, int Y, int W, int H) Cell(int cell, int pagesPerSheet, bool landscapeSheet, int printableWPx, int printableHPx)
    {
        var (cols, rows) = Grid(pagesPerSheet, landscapeSheet);
        int cw = printableWPx / cols, ch = printableHPx / rows;
        int c = cell % cols, r = cell / cols;
        return (c * cw, r * ch, c == cols - 1 ? printableWPx - c * cw : cw, r == rows - 1 ? printableHPx - r * ch : ch);
    }
}
