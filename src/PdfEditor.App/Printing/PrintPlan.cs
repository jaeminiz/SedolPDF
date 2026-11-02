namespace PdfEditor.App.Printing;

/// <summary>용지 한 장에 찍을 쪽들(왼쪽 위부터 가로 순서). null 칸은 비워 둔다.</summary>
public sealed record PrintSheet(int?[] Cells);

/// <summary>쪽 목록 → 용지(장) 목록. 소프트웨어 매수(프린터가 매수를 처리하지 못할 때)도 여기서 펼친다.</summary>
public static class PrintPlan
{
    /// <param name="softwareCopies">이 프로그램이 직접 반복할 매수(프린터에 맡겼으면 1).</param>
    /// <param name="duplex">
    /// 양면이면 장 수가 홀수인 한 부 뒤에 빈 장을 끼워 다음 부가 앞 부의 뒷면에 찍히지 않게 한다.
    /// 또 양면에서는 한 부씩이 아니어도 한 부씩으로 본다(같은 쪽 여러 장이 앞뒤에 찍히는 것을 막는다).
    /// </param>
    public static IReadOnlyList<PrintSheet> Build(IReadOnlyList<int> pages, int pagesPerSheet, int softwareCopies, bool collate, bool duplex)
    {
        int n = pagesPerSheet is 2 or 4 ? pagesPerSheet : 1;
        var one = new List<PrintSheet>();
        for (int i = 0; i < pages.Count; i += n)
        {
            var cells = new int?[n];
            for (int c = 0; c < n && i + c < pages.Count; c++) cells[c] = pages[i + c];
            one.Add(new PrintSheet(cells));
        }
        int copies = Math.Max(1, softwareCopies);
        if (one.Count == 0 || copies == 1) return one;

        var all = new List<PrintSheet>();
        if (collate || duplex)
        {
            for (int k = 0; k < copies; k++)
            {
                all.AddRange(one);
                if (duplex && one.Count % 2 == 1 && k < copies - 1) all.Add(new PrintSheet(new int?[n]));
            }
        }
        else
        {
            foreach (var s in one)
                for (int k = 0; k < copies; k++) all.Add(s);
        }
        return all;
    }

    /// <summary>실제로 그릴 쪽 칸의 수(진행 표시용).</summary>
    public static int CountPages(IReadOnlyList<PrintSheet> sheets) => sheets.Sum(s => s.Cells.Count(c => c.HasValue));
}
