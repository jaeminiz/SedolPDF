using PdfEditor.Document;

namespace PdfEditor.Pages;

public static class PageOrder
{
    /// <summary>
    /// 문서 순서를 target과 같게 만든다 (i번째가 target[i]가 아니면 그 쪽을 i로 옮김).
    /// 전용 스레드 전용. 페이지 집합이 target과 같아야 한다.
    /// </summary>
    public static void ReorderTo(DocumentSession s, IReadOnlyList<PageId> target)
    {
        for (int i = 0; i < target.Count; i++)
        {
            int j = s.IndexOf(target[i]);
            if (j < 0) throw new ArgumentException("문서에 없는 페이지가 포함되어 있습니다.", nameof(target));
            if (j == i) continue;
            s.Document.MovePages([j], i);
            s.MovePageIds([target[i]], i);
        }
    }
}
