using Avalonia;
using PdfEditor.App.Shell;

namespace PdfEditor.App.Hosting;

/// <summary>창 하나의 탭 줄: 화면 물리 픽셀 사각형, 탭들의 탭 줄 안 위치(DIP), 그 창의 화면 배율.</summary>
public readonly record struct StripInfo(object Window, PixelRect StripRect, IReadOnlyList<(double Left, double Width)> Tabs, double Scaling);

/// <summary>탭을 놓았을 때 할 일: 없음 / 같은 창 순서 바꾸기 / 다른 창 탭 줄에 넣기 / 새 창으로 끌어내기.</summary>
public enum DropKind { None, Reorder, OtherWindow, NewWindow }

/// <param name="Window">Reorder·OtherWindow일 때 그 창, 아니면 null.</param>
/// <param name="Slot">탭 줄에서 놓일 자리(0..탭 수), 탭 줄 밖이면 -1.</param>
public readonly record struct DropTarget(DropKind Kind, object? Window, int Slot);

/// <summary>탭을 끄는 동안 포인터 위치(화면 물리 픽셀)로 놓을 곳을 정하는 순수 계산(0.8.0 여러 창 설계 6).</summary>
public static class DropTargetFinder
{
    /// <summary>탭 높이. 탭 줄에서 세로로 이만큼(DIP) 벗어나기 전까지는 탭 줄 안으로 본다(살짝 흔들려도 끌어내지 않게).</summary>
    public const double TearOffDistanceDip = 32;

    /// <param name="strips">최근 활성 창 먼저(창이 겹치면 앞 창이 이긴다).</param>
    /// <param name="source">끌기를 시작한 창.</param>
    /// <param name="sourceTabCount">시작한 창의 탭 수. 1이면 새 창으로 끌어내도 모양이 같으므로 끌어내지 않는다.</param>
    public static DropTarget Find(PixelPoint screen, object source, IReadOnlyList<StripInfo> strips, int sourceTabCount)
    {
        foreach (var s in strips)
        {
            double margin = TearOffDistanceDip * s.Scaling;
            var r = s.StripRect;
            if (screen.X < r.X || screen.X >= r.Right || screen.Y < r.Y - margin || screen.Y > r.Bottom + margin) continue;
            int slot = TabReorder.Slot(s.Tabs, (screen.X - r.X) / s.Scaling);
            return new(ReferenceEquals(s.Window, source) ? DropKind.Reorder : DropKind.OtherWindow, s.Window, slot);
        }
        return sourceTabCount > 1 ? new(DropKind.NewWindow, null, -1) : new(DropKind.None, null, -1);
    }
}
