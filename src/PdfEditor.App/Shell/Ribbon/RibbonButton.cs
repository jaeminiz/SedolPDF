using Avalonia;
using Avalonia.Controls;

namespace PdfEditor.App.Shell.Ribbon;

/// <summary>리본 버튼. 큰 버튼은 아이콘 위·이름 아래, 작은 버튼은 아이콘 옆에 이름. 이름은 항상 보이고 아이콘은 없어도 된다.</summary>
public class RibbonButton : Button
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<RibbonButton, string?>(nameof(Label));
    public static readonly StyledProperty<string?> IconKeyProperty = AvaloniaProperty.Register<RibbonButton, string?>(nameof(IconKey));
    public static readonly StyledProperty<bool> IsLargeProperty = AvaloniaProperty.Register<RibbonButton, bool>(nameof(IsLarge), true);
    public static readonly StyledProperty<bool> IsCheckedProperty = AvaloniaProperty.Register<RibbonButton, bool>(nameof(IsChecked));

    static RibbonButton()
    {
        IsLargeProperty.Changed.AddClassHandler<RibbonButton>((b, _) => b.UpdatePseudo());
        IsCheckedProperty.Changed.AddClassHandler<RibbonButton>((b, _) => b.UpdatePseudo());
    }

    public RibbonButton() => UpdatePseudo();

    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? IconKey { get => GetValue(IconKeyProperty); set => SetValue(IconKeyProperty, value); }
    public bool IsLarge { get => GetValue(IsLargeProperty); set => SetValue(IsLargeProperty, value); }
    /// <summary>선택된 도구 표시 (연한 파랑 배경).</summary>
    public bool IsChecked { get => GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }

    private void UpdatePseudo()
    {
        PseudoClasses.Set(":small", !IsLarge);
        PseudoClasses.Set(":checked", IsChecked);
    }

    protected override Type StyleKeyOverride => typeof(RibbonButton);
}
