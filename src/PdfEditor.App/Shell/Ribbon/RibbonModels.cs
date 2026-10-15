namespace PdfEditor.App.Shell.Ribbon;

/// <summary>리본 정의(문구는 Strings에서). 명령 Id는 CommandRegistry에 모두 있어야 한다.</summary>
public sealed record RibbonItemModel(string CommandId, string Label, string? DisabledTip = null);
public sealed record RibbonGroupModel(string Name, IReadOnlyList<RibbonItemModel> Items, bool Compact = false);

/// <summary>화면에 바인딩되는 리본 버튼.</summary>
public sealed record RibbonButtonVm(AppCommand Command, string Label, bool IsLarge, string? Tip);
public sealed record RibbonGroupVm(string Name, IReadOnlyList<RibbonButtonVm> Items, bool Compact);
