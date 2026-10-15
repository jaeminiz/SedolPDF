using CommunityToolkit.Mvvm.ComponentModel;

namespace PdfEditor.App.Documents;

/// <summary>문서 탭 하나. Task 10에서는 최소 형태이고, Task 11에서 세션·보기 모드·도구·선택 페이지를 채운다.</summary>
public sealed partial class DocumentTabViewModel : ObservableObject
{
    [ObservableProperty] private string _title;
    [ObservableProperty] private double _scale = 1.0;
    [ObservableProperty] private int _currentPageIndex;
    [ObservableProperty] private int _pageCount;
    [ObservableProperty] private double _pageWidthMm;
    [ObservableProperty] private double _pageHeightMm;

    public DocumentTabViewModel(string title) => _title = title;
}
