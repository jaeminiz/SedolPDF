using Avalonia.Controls;

namespace PdfEditor.App.Printing;

/// <summary>인쇄 옵션(주석 포함/제외, 용지에 맞춤/실제 크기, 자동 회전). 취소하면 null.</summary>
public sealed partial class PrintOptionsDialog : Window
{
    public PrintOptionsDialog()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close(new PrintOptions(
            WithAnnotations: WithAnnotRadio.IsChecked == true,
            Scaling: ActualRadio.IsChecked == true ? PrintScaling.ActualSize : PrintScaling.FitToPaper,
            AutoRotate: AutoRotateBox.IsChecked == true));
        CancelButton.Click += (_, _) => Close(null);
    }
}
