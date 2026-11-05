using Avalonia.Controls;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Dialogs;

/// <summary>목표 용량(MB) 입력. 확인하면 바이트(long), 취소하면 null을 돌려준다. 잘못된 입력은 창 안에서 알리고 닫지 않는다.</summary>
public sealed partial class ReduceTargetDialog : Window
{
    public ReduceTargetDialog() => InitializeComponent();

    public ReduceTargetDialog(long currentBytes) : this()
    {
        if (currentBytes > 0)
        {
            CurrentText.Text = string.Format(Strings.Dlg_ReduceCurrent, ByteSize.Format(currentBytes));
            CurrentText.IsVisible = true;
        }
        Preset8.Click += (_, _) => SizeBox.Text = "8";
        Preset16.Click += (_, _) => SizeBox.Text = "16";
        Preset20.Click += (_, _) => SizeBox.Text = "20";
        SizeBox.TextChanged += (_, _) => InvalidText.IsVisible = false;
        OkButton.Click += (_, _) =>
        {
            if (ByteSize.TryParseMegabytes(SizeBox.Text, out long bytes)) Close(bytes);
            else InvalidText.IsVisible = true;
        };
        CancelButton.Click += (_, _) => Close(null);
        Opened += (_, _) => { SizeBox.Focus(); SizeBox.SelectAll(); };
    }
}
