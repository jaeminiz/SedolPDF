using Avalonia;
using PdfEditor.App.Hosting;
using Xunit;

namespace PdfEditor.App.Tests.Hosting;

/// <summary>0.8.0 단계 5: 새 창이 화면(작업 영역) 밖으로 나가지 않게 옮기기·줄이기.</summary>
public class WindowPlacementTests
{
    private static readonly PixelRect Main = new(0, 0, 1920, 1040);          // 작업 표시줄 뺀 영역
    private static readonly PixelRect Second = new(1920, 0, 2560, 1400);

    [Fact]
    public void InsideWorkArea_Unchanged()
    {
        var r = new PixelRect(100, 100, 800, 600);
        Assert.Equal(r, WindowPlacement.FitToWorkArea(r, [Main, Second]));
    }

    [Fact]
    public void OffRightEdge_ShiftedIn()
    {
        Assert.Equal(new PixelRect(1120, 100, 800, 600), WindowPlacement.FitToWorkArea(new PixelRect(1500, 100, 800, 600), [Main]));
        // 위·왼쪽으로 넘쳐도 안으로
        Assert.Equal(new PixelRect(0, 0, 800, 600), WindowPlacement.FitToWorkArea(new PixelRect(-300, -50, 800, 600), [Main]));
        // 아래로 넘침
        Assert.Equal(new PixelRect(100, 440, 800, 600), WindowPlacement.FitToWorkArea(new PixelRect(100, 900, 800, 600), [Main]));
    }

    [Fact]
    public void LargerThanArea_Shrunk()
    {
        Assert.Equal(Main, WindowPlacement.FitToWorkArea(new PixelRect(50, 50, 2500, 1500), [Main]));
    }

    [Fact]
    public void OnSecondMonitor_StaysOnSecond()
    {
        var r = new PixelRect(3000, 100, 1200, 800);
        Assert.Equal(r, WindowPlacement.FitToWorkArea(r, [Main, Second]));
        // 두 모니터에 걸치면 더 많이 걸친 쪽(둘째) 안으로
        Assert.Equal(new PixelRect(1920, 100, 1200, 800), WindowPlacement.FitToWorkArea(new PixelRect(1800, 100, 1200, 800), [Main, Second]));
    }

    [Fact]
    public void NoArea_UsesFirst()
    {
        Assert.Equal(new PixelRect(1120, 440, 800, 600), WindowPlacement.FitToWorkArea(new PixelRect(9000, 9000, 800, 600), [Main, Second]));
        // 작업 영역을 모르면 그대로
        var r = new PixelRect(9000, 9000, 800, 600);
        Assert.Equal(r, WindowPlacement.FitToWorkArea(r, []));
    }
}
