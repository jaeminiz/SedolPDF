using Avalonia.Controls;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Dialogs;

/// <summary>도움말: 기본 사용법과 단축키(문구는 Strings). 자세한 매뉴얼은 이후 단계.</summary>
public sealed partial class HelpDialog : Window
{
    public HelpDialog()
    {
        InitializeComponent();
        Title = Strings.Dlg_HelpTitle;
        BasicsTitle.Text = Strings.Help_Basics;
        BasicsText.Text = Strings.Help_BasicsText;
        ShortcutsTitle.Text = Strings.Help_Shortcuts;
        ShortcutsText.Text = Strings.Help_ShortcutsText;
        NoteText.Text = Strings.Help_Note;
        CloseButton.Content = Strings.Dlg_AboutClose;
        CloseButton.Click += (_, _) => Close();
    }
}
