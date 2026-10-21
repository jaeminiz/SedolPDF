namespace PdfEditor.App.Shell;

/// <summary>탭 끌어 순서 바꾸기의 순수 계산(화면 없이 시험할 수 있게 분리).</summary>
public static class TabReorder
{
    /// <summary>
    /// 끌고 있는 위치 x가 놓일 자리(0..count). 각 탭의 가운데보다 왼쪽이면 그 탭 앞.
    /// </summary>
    public static int Slot(IReadOnlyList<(double Left, double Width)> tabs, double x)
    {
        for (int i = 0; i < tabs.Count; i++)
            if (x < tabs[i].Left + tabs[i].Width / 2) return i;
        return tabs.Count;
    }

    /// <summary>from번 탭을 slot 자리(그 번호 탭 앞)에 놓았을 때 탭의 새 번호.</summary>
    public static int FinalIndex(int from, int slot, int count)
    {
        slot = Math.Clamp(slot, 0, count);
        return slot > from ? slot - 1 : slot;
    }
}
