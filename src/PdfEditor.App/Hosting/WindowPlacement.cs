using Avalonia;

namespace PdfEditor.App.Hosting;

/// <summary>새 창 자리 보정의 순수 계산(화면 물리 픽셀). 끌어낸 탭으로 만든 창이 화면 밖에 뜨지 않게 한다.</summary>
public static class WindowPlacement
{
    /// <summary>
    /// wanted를 가장 많이 걸친 작업 영역 안으로 옮기고, 그 영역보다 크면 줄인다. 어느 영역에도 걸치지 않으면 첫째 영역,
    /// 작업 영역을 모르면 그대로.
    /// </summary>
    public static PixelRect FitToWorkArea(PixelRect wanted, IReadOnlyList<PixelRect> workAreas)
    {
        if (workAreas.Count == 0) return wanted;
        var area = workAreas[0];
        long best = 0;
        foreach (var a in workAreas)
        {
            var i = a.Intersect(wanted);
            long size = (long)i.Width * i.Height;
            if (size > best) { best = size; area = a; }
        }
        int w = Math.Min(wanted.Width, area.Width), h = Math.Min(wanted.Height, area.Height);
        int x = Math.Clamp(wanted.X, area.X, area.Right - w), y = Math.Clamp(wanted.Y, area.Y, area.Bottom - h);
        return new PixelRect(x, y, w, h);
    }
}
