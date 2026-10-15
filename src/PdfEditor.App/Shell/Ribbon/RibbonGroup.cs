using Avalonia;
using Avalonia.Controls;

namespace PdfEditor.App.Shell.Ribbon;

/// <summary>리본 그룹: 버튼들 + 아래 작은 그룹 이름. Compact면 버튼을 작은 크기로 세로 두 줄 이하로 쌓는다.</summary>
public sealed class RibbonGroup : ItemsControl
{
    public static readonly StyledProperty<string?> HeaderProperty = AvaloniaProperty.Register<RibbonGroup, string?>(nameof(Header));
    public static readonly StyledProperty<bool> IsCompactProperty = AvaloniaProperty.Register<RibbonGroup, bool>(nameof(IsCompact));

    static RibbonGroup() => IsCompactProperty.Changed.AddClassHandler<RibbonGroup>((g, _) => g.PseudoClasses.Set(":compact", g.IsCompact));

    public string? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public bool IsCompact { get => GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    protected override Type StyleKeyOverride => typeof(RibbonGroup);
}
