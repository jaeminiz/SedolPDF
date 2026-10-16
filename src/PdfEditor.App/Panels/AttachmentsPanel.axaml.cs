using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PdfEditor.App.Panels;

/// <summary>첨부 패널. "저장"을 누르면 모델이 안전한 파일 이름을 제안하고 선택한 위치에만 쓴다.</summary>
public sealed partial class AttachmentsPanel : UserControl
{
    public AttachmentsPanel() => InitializeComponent();

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AttachmentItem item } && DataContext is AttachmentsViewModel vm)
            await vm.SaveAsync(item);
    }
}
