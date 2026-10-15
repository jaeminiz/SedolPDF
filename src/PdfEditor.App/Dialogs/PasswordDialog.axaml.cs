using Avalonia.Controls;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Dialogs;

/// <summary>암호 입력. 확인하면 입력한 문자열, 취소하면 null을 돌려준다.</summary>
public sealed partial class PasswordDialog : Window
{
    public PasswordDialog() => InitializeComponent();

    public PasswordDialog(string fileName, bool retry) : this()
    {
        PromptText.Text = string.Format(Strings.Dlg_PasswordPrompt, fileName);
        RetryText.IsVisible = retry;
        OkButton.Click += (_, _) => Close(PasswordBox.Text ?? "");
        CancelButton.Click += (_, _) => Close(null);
        Opened += (_, _) => PasswordBox.Focus();
    }
}
