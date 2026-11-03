using PdfEditor.Pages;

namespace PdfEditor.App.Printing;

/// <summary>쪽 범위 옵션을 인쇄할 쪽 번호(0부터)로 바꾼다.</summary>
public static class PrintPageSelection
{
    /// <summary>
    /// 홀수·짝수는 쪽 번호(1부터)의 홀짝이다 — "선택한 쪽 3,4,5 + 홀수만" = 3쪽·5쪽.
    /// 직접 입력이 비었거나 문법·범위가 틀리면 false(결과 비어 있음). 고른 쪽이 없는 "선택한 쪽"은 true + 빈 결과(대화상자가 인쇄 단추를 막는다).
    /// 선택한 쪽은 문서 순서로, 직접 입력은 입력한 순서 그대로("5,1-2" → 5·1·2쪽).
    /// </summary>
    public static bool TryResolve(PrintOptions o, int pageCount, int currentIndex, IReadOnlyCollection<int> selectedIndices, out int[] indices)
    {
        indices = [];
        if (pageCount <= 0) return true;
        IEnumerable<int> baseSet;
        switch (o.Range)
        {
            case PrintRangeMode.Current:
                baseSet = [Math.Clamp(currentIndex, 0, pageCount - 1)];
                break;
            case PrintRangeMode.Selected:
                baseSet = selectedIndices.Where(i => i >= 0 && i < pageCount).Distinct().OrderBy(i => i);
                break;
            case PrintRangeMode.Custom:
                if (string.IsNullOrWhiteSpace(o.RangeText)) return false;
                if (!PageRangeParser.TryParse(o.RangeText, pageCount, out var parsed)) return false;
                baseSet = parsed;
                break;
            default:
                baseSet = Enumerable.Range(0, pageCount);
                break;
        }
        indices = o.Parity switch
        {
            PrintParity.Odd => baseSet.Where(i => i % 2 == 0).ToArray(),
            PrintParity.Even => baseSet.Where(i => i % 2 == 1).ToArray(),
            _ => baseSet.ToArray(),
        };
        return true;
    }
}
