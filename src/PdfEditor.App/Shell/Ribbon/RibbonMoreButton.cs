using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PdfEditor.App.Shell.Ribbon;

/// <summary>
/// "더보기 ▾" 단추: 눌러서 메뉴를 열고, 항목은 도구 단추와 같은 명령을 부른다(켜진 도구는 체크 표시).
/// 메뉴는 열릴 때마다 새로 만든다(체크 상태·실행 가능 여부가 그때 값이 되도록).
/// </summary>
public sealed class RibbonMoreButton : RibbonButton
{
    public static readonly StyledProperty<IReadOnlyList<RibbonButtonVm>?> ItemsProperty =
        AvaloniaProperty.Register<RibbonMoreButton, IReadOnlyList<RibbonButtonVm>?>(nameof(Items));

    private readonly MenuFlyout _flyout = new();

    public RibbonMoreButton()
    {
        _flyout.Opening += (_, _) => Rebuild();
        Flyout = _flyout;
    }

    public IReadOnlyList<RibbonButtonVm>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    /// <summary>메뉴 항목 만들기 (열기 전과 테스트에서 부른다).</summary>
    public void Rebuild()
    {
        _flyout.Items.Clear();
        foreach (var vm in Items ?? [])
        {
            var item = new MenuItem
            {
                Header = vm.Label,
                Command = vm.Command.Command,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = vm.IsChecked,
            };
            _flyout.Items.Add(item);
        }
    }

    public IReadOnlyList<MenuItem> MenuItems => [.. _flyout.Items.OfType<MenuItem>()];

    protected override Type StyleKeyOverride => typeof(RibbonButton);
}
