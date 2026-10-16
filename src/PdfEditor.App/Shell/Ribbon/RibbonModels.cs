using CommunityToolkit.Mvvm.ComponentModel;
using PdfEditor.App.Documents;

namespace PdfEditor.App.Shell.Ribbon;

/// <summary>리본 정의(문구는 Strings에서). 명령 Id는 CommandRegistry에 모두 있어야 한다. Tool: 이 단추가 고르는 도구(켜진 표시용).</summary>
public sealed record RibbonItemModel(string CommandId, string Label, string? DisabledTip = null, ToolKind? Tool = null);
public sealed record RibbonGroupModel(string Name, IReadOnlyList<RibbonItemModel> Items, bool Compact = false);

/// <summary>화면에 바인딩되는 리본 버튼. IsChecked: 활성 문서의 도구가 이 단추의 도구와 같을 때 true.</summary>
public sealed partial class RibbonButtonVm(AppCommand command, string label, bool isLarge, string? tip, ToolKind? tool = null)
    : ObservableObject
{
    public AppCommand Command { get; } = command;
    public string Label { get; } = label;
    public bool IsLarge { get; } = isLarge;
    public string? Tip { get; } = tip;
    public ToolKind? Tool { get; } = tool;
    [ObservableProperty] private bool _isChecked;
}

public sealed record RibbonGroupVm(string Name, IReadOnlyList<RibbonButtonVm> Items, bool Compact);
