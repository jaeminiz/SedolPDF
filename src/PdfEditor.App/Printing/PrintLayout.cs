namespace PdfEditor.App.Printing;

/// <summary>FPDF_RenderPage에 넘길 위치·크기(인쇄 가능 영역 왼쪽 위 기준 픽셀). Width/Height는 회전을 적용한 뒤의 크기.</summary>
public readonly record struct PrintPlacement(int X, int Y, int Width, int Height, int Rotate);

public static class PrintLayout
{
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
        if (o.Scaling == PrintScaling.FitToPaper && dw > 0 && dh > 0)
        {
            double k = Math.Min(printableWPx / dw, printableHPx / dh);
            dw *= k;
            dh *= k;
        }
        int width = Math.Max(1, (int)Math.Round(dw));
        int height = Math.Max(1, (int)Math.Round(dh));
        int x = (int)Math.Round((printableWPx - dw) / 2.0);
        int y = (int)Math.Round((printableHPx - dh) / 2.0);
        return new PrintPlacement(x, y, width, height, rotate ? 1 : 0);
    }
}
