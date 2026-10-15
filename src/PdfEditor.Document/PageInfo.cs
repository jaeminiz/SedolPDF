namespace PdfEditor.Document;

/// <summary>UI 스레드에서 읽어도 되는 페이지 스냅숏. 크기는 회전이 반영된 포인트.</summary>
public sealed record PageInfo(PageId Id, int Version, float WidthPt, float HeightPt);

/// <summary>Changed: 새로 생겼거나 내용(버전·크기)이 바뀐 페이지. StructureChanged: 쪽 수나 순서가 바뀜.</summary>
public sealed record PagesChangedEventArgs(IReadOnlyList<PageId> Changed, bool StructureChanged);
