using Avalonia;

namespace PdfEditor.App.Panels;

/// <summary>
/// 썸네일 칸을 같은 크기로 가로로 채워 놓는 배치 계산(UI 없음). 폭이 칸 폭의 몇 배인지로 열 수가 정해지고,
/// 남는 폭은 양쪽에 나눠 가운데로 맞춘다. 패널(ThumbnailGridPanel)과 끌어 옮기기 위치 계산이 같은 식을 쓴다.
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

    public static Rect CellRect(int index, int columns, double width, double cellWidth, double cellHeight) =>
        new(LeftOffset(width, columns, cellWidth) + index % columns * cellWidth, index / columns * cellHeight, cellWidth, cellHeight);

    /// <summary>top..bottom(패널 좌표) 구간에 걸치는 칸 번호 범위(양 끝 포함, 위아래로 bufferRows줄씩 더). 비면 Last &lt; First.</summary>
    public static (int First, int Last) VisibleRange(double top, double bottom, double cellHeight, int columns, int count, int bufferRows = 1)
    {
        if (count <= 0 || cellHeight <= 0) return (0, -1);
        int rows = Rows(count, columns);
        int firstRow = Math.Clamp((int)Math.Floor(top / cellHeight) - bufferRows, 0, rows - 1);
        int lastRow = Math.Clamp((int)Math.Floor(bottom / cellHeight) + bufferRows, firstRow, rows - 1);
        return (firstRow * columns, Math.Min(count - 1, (lastRow + 1) * columns - 1));
    }

    /// <summary>
    /// 점 p(패널 좌표)에 해당하는 끌어 놓을 자리. 한 열이면 칸의 위·아래 절반, 여러 열이면 왼쪽·오른쪽 절반으로 앞/뒤를 정한다.
    /// 칸이 없는 빈 곳(마지막 줄 오른쪽·맨 아래)은 맨 끝. 막대는 여러 열이면 세로선, 한 열이면 가로선.
    /// </summary>
    public static DropSlot DropSlotAt(Point p, int count, int columns, double width, double cellWidth, double cellHeight)
    {
        if (count <= 0) return new DropSlot(0, columns == 1 ? new Rect(0, 0, width, CaretThickness) : new Rect(LeftOffset(width, columns, cellWidth), 0, CaretThickness, cellHeight));
        int row = Math.Max(0, (int)Math.Floor(p.Y / cellHeight));
        double left = LeftOffset(width, columns, cellWidth);
        int col = Math.Clamp((int)Math.Floor((p.X - left) / cellWidth), 0, columns - 1);
        int idx = row * columns + col;
        bool after;
        if (idx >= count) { idx = count - 1; after = true; }
        else if (columns == 1) after = p.Y - row * cellHeight > cellHeight / 2;
        else after = p.X - left - col * cellWidth > cellWidth / 2;
        var cell = CellRect(idx, columns, width, cellWidth, cellHeight);
        int slot = idx + (after ? 1 : 0);
        Rect caret = columns == 1
            ? new Rect(0, Math.Max(0, (after ? cell.Bottom : cell.Top) - CaretThickness / 2), width, CaretThickness)
            : new Rect(Math.Max(0, (after ? cell.Right : cell.Left) - CaretThickness / 2), cell.Top, CaretThickness, cell.Height);
        return new DropSlot(slot, caret);
    }
}
