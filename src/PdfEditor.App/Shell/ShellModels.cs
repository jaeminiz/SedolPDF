using CommunityToolkit.Mvvm.ComponentModel;

namespace PdfEditor.App.Shell;

public enum PanelKind { Thumbnails, Bookmarks, Comments, Attachments, Layers, Search }

/// <summary>리본 탭 머리글. 선택되면 아래에 강조색 줄이 생긴다.</summary>
public sealed partial class RibbonTabVm(string id, string label) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>왼쪽 패널 전환 막대의 버튼.</summary>
public sealed partial class PanelButtonVm(PanelKind kind, string label, string iconKey) : ObservableObject
{
    public PanelKind Kind { get; } = kind;
    public string Label { get; } = label;
    public string IconKey { get; } = iconKey;
    [ObservableProperty] private bool _isSelected;
}
