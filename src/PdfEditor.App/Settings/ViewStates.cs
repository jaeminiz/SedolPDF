using PdfEditor.App.Viewer;

namespace PdfEditor.App.Settings;

/// <summary>파일마다 마지막으로 보던 쪽·배율·보기 방식(0.4.0 Task 11). Path는 전체 경로, PageCount는 저장 때의 쪽 수(파일이 바뀌었는지 가늠하는 용도).</summary>
public sealed record ViewStateEntry(string Path, int Page, double Scale, string Mode, int PageCount);

/// <summary>최근 파일별 보기 상태 목록 규칙: 최근 것이 앞, 같은 경로(대소문자 무시)는 하나, 최대 <see cref="Max"/>개.</summary>
public static class ViewStates
{
    public const int Max = 50;

    public static IReadOnlyList<ViewStateEntry> Upsert(IReadOnlyList<ViewStateEntry>? list, ViewStateEntry entry)
    {
        var result = new List<ViewStateEntry> { entry };
        if (list is not null)
            result.AddRange(list.Where(e => e is not null && !string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase)));
        return result.Take(Max).ToList();
    }

    public static ViewStateEntry? Find(IReadOnlyList<ViewStateEntry>? list, string path) =>
        list?.FirstOrDefault(e => e is not null && string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 저장된 상태를 지금 문서에 적용해도 되는지: 쪽 수가 같고(파일이 바뀌지 않았다는 가정) 쪽 번호·배율·방식이 올바른 범위일 때만.
    /// 맞지 않으면 null(= 처음 열 때처럼 보인다).
    /// </summary>
    public static ViewStateEntry? Validate(ViewStateEntry? e, int pageCount)
    {
        if (e is null || pageCount <= 0 || e.PageCount != pageCount) return null;
        if (e.Page < 0 || e.Page >= pageCount) return null;
        if (double.IsNaN(e.Scale) || double.IsInfinity(e.Scale) || e.Scale < ZoomMath.MinScale || e.Scale > ZoomMath.MaxScale) return null;
        if (!Enum.TryParse<ViewMode>(e.Mode, out _)) return null;
        return e;
    }
}
