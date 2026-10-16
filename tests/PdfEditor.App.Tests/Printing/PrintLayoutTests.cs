using PdfEditor.App.Printing;

namespace PdfEditor.App.Tests.Printing;

public class PrintLayoutTests
{
    // A4 600dpi 인쇄 가능 영역(여백 약간): 4960 x 7016 px
    private const int PW = 4960, PH = 7016, Dpi = 600;

    [Fact]
    public void Layout_A4OnA4_Fit_FillsPrintable()
    {
        var p = PrintLayout.Compute(595.28f, 841.89f, PW, PH, Dpi, Dpi, new PrintOptions());
        Assert.Equal(0, p.Rotate);
        Assert.InRange(p.Width, PW - 1, PW + 1);
        Assert.InRange(p.Height, PH - 1, PH + 1);
        Assert.InRange(p.X, -1, 1);
        Assert.InRange(p.Y, -1, 1);
    }

    [Fact]
    public void Layout_A1LandscapeOnA4Portrait_AutoRotates_AndFits()
    {
        var p = PrintLayout.Compute(2383.94f, 1683.78f, PW, PH, Dpi, Dpi, new PrintOptions());
        Assert.Equal(1, p.Rotate);
        Assert.True(p.Width <= PW + 1 && p.Height <= PH + 1);
        // 회전 후 세로형: 가로 또는 세로 한쪽이 꽉 차고 나머지는 가운데 정렬
        Assert.True(Math.Abs(p.Width - PW) <= 1 || Math.Abs(p.Height - PH) <= 1);
        Assert.InRange(p.X * 2 + p.Width, PW - 2, PW + 2);
        Assert.InRange(p.Y * 2 + p.Height, PH - 2, PH + 2);
        // 비율 유지
        Assert.Equal(2383.94 / 1683.78, (double)p.Height / p.Width, 3);
    }

    [Fact]
    public void Layout_ActualSize_A4At600Dpi()
    {
        var p = PrintLayout.Compute(595.28f, 841.89f, PW, PH, Dpi, Dpi, new PrintOptions(Scaling: PrintScaling.ActualSize));
        Assert.InRange(p.Width, 4961 - 1, 4961 + 1);
        Assert.InRange(p.Height, 7016 - 1, 7016 + 1 + 1);
        Assert.Equal(0, p.Rotate);
    }

    [Fact]
    public void Layout_ActualSize_LargerThanPaper_IsCenteredAndCropped()
    {
        var p = PrintLayout.Compute(1190.55f, 1683.78f, PW, PH, Dpi, Dpi, new PrintOptions(Scaling: PrintScaling.ActualSize));
        Assert.True(p.Width > PW);
        Assert.True(p.X < 0);
        Assert.InRange(p.X * 2 + p.Width, PW - 2, PW + 2);
    }

    [Fact]
    public void Layout_AutoRotateOff_NoRotate()
    {
        var p = PrintLayout.Compute(2383.94f, 1683.78f, PW, PH, Dpi, Dpi, new PrintOptions(AutoRotate: false));
        Assert.Equal(0, p.Rotate);
        Assert.InRange(p.Width, PW - 1, PW + 1);
        Assert.True(p.Height < PH / 2);
    }

    [Fact]
    public void Layout_SameOrientation_NoRotate()
    {
        var p = PrintLayout.Compute(841.89f, 595.28f, PH, PW, Dpi, Dpi, new PrintOptions());
        Assert.Equal(0, p.Rotate);
    }
}
