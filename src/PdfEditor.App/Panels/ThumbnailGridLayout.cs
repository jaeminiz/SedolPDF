using Avalonia;

namespace PdfEditor.App.Panels;

/// <summary>
/// 썸네일 칸을 가로로 채워 놓는 배치 계산(UI 없음). 칸 폭은 모두 같고, 폭이 칸 폭의 몇 배인지로 열 수가 정해지며
/// 남는 폭은 양쪽에 나눠 가운데로 맞춘다. 줄 높이는 줄마다 다르다(가로 쪽만 있는 줄은 낮다, Acrobat처럼).
/// rowTops[r]는 r번째 줄의 위쪽 y, rowTops[줄 수]는 전체 높이다. 패널(ThumbnailGridPanel)과 끌어 옮기기 위치 계산이 같은 식을 쓴다.
/// </summary>
public static class ThumbnailGridLayout
{
    /// <summary>끌어 놓을 자리: Index는 "그 번호 앞"(0..count), Caret은 그 자리를 보여 주는 막대(패널 좌표).</summary>
    public readonly record struct DropSlot(int Index, Rect Caret);

    public const double CaretThickness = 3;

    public static int Columns(double width, double cellWidth) =>
        double.IsFinite(width) && cellWidth > 0 ? Math.Max(1, (int)Math.Floor(width / cellWidth)) : 1;

    public static int Rows(int count, int columns) => count <= 0 ? 0 : (count + columns - 1) / columns;

    public static double LeftOffset(double width, int columns, double cellWidth) =>
        double.IsFinite(width) ? Math.Max(0, (width - columns * cellWidth) / 2) : 0;

    /// <summary>줄 높이들로 줄 위쪽 y 목록(길이 = 줄 수 + 1)을 만든다.</summary>
    public static double[] RowTops(IReadOnlyList<double> rowHeights)
    {
        var tops = new double[rowHeights.Count + 1];
        for (int i = 0; i < rowHeights.Count; i++) tops[i + 1] = tops[i] + rowHeights[i];
        return tops;
    }

    /// <summary>모든 줄 높이가 같은 경우의 줄 위쪽 y 목록.</summary>
    public static double[] UniformRowTops(int rows, double rowHeight) => RowTops(Enumerable.Repeat(rowHeight, rows).ToArray());

    /// <summary>y가 속한 줄 번호(0 ~ 줄 수 - 1, 범위 밖은 양 끝 줄). 줄이 없으면 -1.</summary>
    public static int RowAt(double y, double[] rowTops)
    {
        int rows = rowTops.Length - 1;
        if (rows <= 0) return -1;
        int lo = 0, hi = rows - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (rowTops[mid] <= y) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public static Rect CellRect(int index, int columns, double width, double cellWidth, double[] rowTops)
    {
        int row = index / columns;
        return new(LeftOffset(width, columns, cellWidth) + index % columns * cellWidth, rowTops[row], cellWidth, rowTops[row + 1] - rowTops[row]);
    }

    /// <summary>top..bottom(패널 좌표) 구간에 걸치는 칸 번호 범위(양 끝 포함, 위아래로 bufferRows줄씩 더). 비면 Last &lt; First.</summary>
    public static (int First, int Last) VisibleRange(double top, double bottom, double[] rowTops, int columns, int count, int bufferRows = 1)
    {
        int rows = rowTops.Length - 1;
        if (count <= 0 || rows <= 0) return (0, -1);
        int firstRow = Math.Clamp(RowAt(top, rowTops) - bufferRows, 0, rows - 1);
        int lastRow = Math.Clamp(RowAt(bottom, rowTops) + bufferRows, firstRow, rows - 1);
        return (firstRow * columns, Math.Min(count - 1, (lastRow + 1) * columns - 1));
    }

    /// <summary>
    /// 점 p(패널 좌표)에 해당하는 끌어 놓을 자리. 한 열이면 칸의 위·아래 절반, 여러 열이면 왼쪽·오른쪽 절반으로 앞/뒤를 정한다.
    /// 칸이 없는 빈 곳(마지막 줄 오른쪽·맨 아래)은 맨 끝. 막대는 여러 열이면 세로선, 한 열이면 가로선.
    /// </summary>
    public static DropSlot DropSlotAt(Point p, int count, int columns, double width, double cellWidth, double[] rowTops)
    {
        if (count <= 0 || rowTops.Length < 2)
            return new DropSlot(0, columns == 1 ? new Rect(0, 0, width, CaretThickness) : new Rect(LeftOffset(width, columns, cellWidth), 0, CaretThickness, rowTops.Length > 1 ? rowTops[1] : 0));
        int row = p.Y >= rowTops[^1] ? int.MaxValue : RowAt(p.Y, rowTops);
        double left = LeftOffset(width, columns, cellWidth);
        int col = Math.Clamp((int)Math.Floor((p.X - left) / cellWidth), 0, columns - 1);
        long idxL = row == int.MaxValue ? long.MaxValue : (long)row * columns + col;
        int idx;
        bool after;
        if (idxL >= count) { idx = count - 1; after = true; }
        else
        {
            idx = (int)idxL;
            if (columns == 1) after = p.Y - rowTops[row] > (rowTops[row + 1] - rowTops[row]) / 2;
            else after = p.X - left - col * cellWidth > cellWidth / 2;
        }
        var cell = CellRect(idx, columns, width, cellWidth, rowTops);
        int slot = idx + (after ? 1 : 0);
        Rect caret = columns == 1
            ? new Rect(0, Math.Max(0, (after ? cell.Bottom : cell.Top) - CaretThickness / 2), width, CaretThickness)
            : new Rect(Math.Max(0, (after ? cell.Right : cell.Left) - CaretThickness / 2), cell.Top, CaretThickness, cell.Height);
        return new DropSlot(slot, caret);
    }
}
