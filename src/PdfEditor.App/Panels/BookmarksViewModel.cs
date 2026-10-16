using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Panels;

/// <summary>책갈피 한 칸. 목적지가 없으면(PageIndex null) 눌러도 아무 일도 없다.</summary>
public sealed partial class BookmarkNode(string title, int? pageIndex, IReadOnlyList<BookmarkNode> children, bool initiallyOpen)
    : ObservableObject
{
    public string Title { get; } = title;
    public int? PageIndex { get; } = pageIndex;
    public IReadOnlyList<BookmarkNode> Children { get; } = children;
    [ObservableProperty] private bool _isExpanded = initiallyOpen;
}

/// <summary>
/// 책갈피 패널 모델. 패널을 처음 열 때 전용 스레드에서 GetBookmarks()를 한 번만 부른다(읽기 전용, 편집은 범위 밖).
/// 모든 공개 멤버는 UI 스레드에서 쓴다.
/// </summary>
public sealed partial class BookmarksViewModel(DocumentTabViewModel tab) : ObservableObject
{
    private bool _loaded;

    public ObservableCollection<BookmarkNode> Nodes { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty))] private bool _hasLoaded;

    /// <summary>읽었는데 책갈피가 없다.</summary>
    public bool IsEmpty => HasLoaded && Nodes.Count == 0;
    public string EmptyText => Strings.Panel_NoBookmarks;

    public async Task LoadAsync()
    {
        if (_loaded || tab.Session is not { } s) return;
        _loaded = true;
        IReadOnlyList<PdfBookmark> roots;
        try { roots = await s.Thread.InvokeAsync(() => s.Document.GetBookmarks()); }
        catch (Exception) { roots = []; }   // 책갈피를 못 읽어도 문서는 계속 쓴다 (빈 패널)
        foreach (var r in roots) Nodes.Add(Convert(r));
        HasLoaded = true;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static BookmarkNode Convert(PdfBookmark b) =>
        new(b.Title, b.PageIndex, b.Children.Select(Convert).ToList(), b.InitiallyOpen);

    /// <summary>책갈피를 눌렀다. 목적지가 있으면 그 쪽으로 이동한다.</summary>
    public void Navigate(BookmarkNode node)
    {
        if (node.PageIndex is not { } i) return;
        tab.CurrentPageIndex = Math.Clamp(i, 0, Math.Max(0, tab.PageCount - 1));
    }
}
