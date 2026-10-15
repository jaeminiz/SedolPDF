using Avalonia;
using Avalonia.Controls;

namespace PdfEditor.App.Shell.Ribbon;

/// <summary>리본 버튼. 큰 버튼은 아이콘 위·이름 아래, 작은 버튼은 아이콘 옆에 이름. 이름은 항상 보이고 아이콘은 없어도 된다.</summary>
public sealed class RibbonButton : Button
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<RibbonButton, string?>(nameof(Label));
    public static readonly StyledProperty<string?> IconKeyProperty = AvaloniaProperty.Register<RibbonButton, string?>(nameof(IconKey));
    public static readonly StyledProperty<bool> IsLargeProperty = AvaloniaProperty.Register<RibbonButton, bool>(nameof(IsLarge), true);

    static RibbonButton() => IsLargeProperty.Changed.AddClassHandler<RibbonButton>((b, _) => b.UpdatePseudo());

    public RibbonButton() => UpdatePseudo();

    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? IconKey { get => GetValue(IconKeyProperty); set => SetValue(IconKeyProperty, value); }
    public bool IsLarge { get => GetValue(IsLargeProperty); set => SetValue(IsLargeProperty, value); }

    private void UpdatePseudo() => PseudoClasses.Set(":small", !IsLarge);

    protected override Type StyleKeyOverride => typeof(RibbonButton);
}
