using CommunityToolkit.Mvvm.ComponentModel;
using PdfEditor.App.Documents;

namespace PdfEditor.App.Shell.Ribbon;

/// <summary>리본 정의(문구는 Strings에서). 명령 Id는 CommandRegistry에 모두 있어야 한다. Tool: 이 단추가 고르는 도구(켜진 표시용).</summary>
public sealed record RibbonItemModel(string CommandId, string Label, string? DisabledTip = null, ToolKind? Tool = null,
    IReadOnlyList<RibbonItemModel>? MoreItems = null)
{
    /// <summary>작은 단추 묶음(Compact) 안에서도 큰 단추로 보인다: 묶음의 대표 도구(2026-10-20 리본 재구성).</summary>
    public bool Large { get; init; }
}
/// <summary>IconOnly: 작은 단추를 그림만으로(이름은 툴팁) — 그리기 도구 격자처럼 단추가 많은 묶음(2026-10-20 주석 탭 배치).</summary>
public sealed record RibbonGroupModel(string Name, IReadOnlyList<RibbonItemModel> Items, bool Compact = false, bool IconOnly = false);

/// <summary>화면에 바인딩되는 리본 버튼. IsChecked: 활성 문서의 도구가 이 단추의 도구와 같을 때 true.</summary>
public sealed partial class RibbonButtonVm(AppCommand command, string label, bool isLarge, string? tip, ToolKind? tool = null,
    IReadOnlyList<RibbonButtonVm>? moreItems = null)
    : ObservableObject
{
    /// <summary>리본 정의에 적힌 기본 툴팁 (문서 상태에 따른 안내가 없을 때 돌아간다).</summary>
    public string? BaseTip { get; } = tip;
    public AppCommand Command { get; } = command;
    public string Label { get; } = label;
    /// <summary>그림만 보이는 단추(이름은 툴팁으로).</summary>
    public bool IconOnly { get; init; }
    public string DisplayLabel => IconOnly ? "" : Label;
    public bool IsLarge { get; } = isLarge;
    /// <summary>툴팁. 암호 문서에서 패치 도구가 꺼진 이유처럼 활성 문서에 따라 바뀐다.</summary>
    [ObservableProperty] private string? _tip = tip;
    public ToolKind? Tool { get; } = tool;
    /// <summary>"더보기 ▾" 단추의 메뉴 항목 (도구 항목은 켜진 도구에 체크 표시). 일반 단추는 null.</summary>
    public IReadOnlyList<RibbonButtonVm>? MoreItems { get; } = moreItems;
    public bool IsMore => MoreItems is not null;
    [ObservableProperty] private bool _isChecked;
}

public sealed record RibbonGroupVm(string Name, IReadOnlyList<RibbonButtonVm> Items, bool Compact);
