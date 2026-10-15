using Avalonia.Controls;
using Avalonia.Input;

namespace PdfEditor.App.Dialogs;

/// <summary>메시지와 단추 몇 개를 보여 주고 누른 단추의 번호를 돌려주는 대화상자. 창을 닫으면 -1.</summary>
public sealed partial class MessageDialog : Window
{
    public MessageDialog() => InitializeComponent();

    /// <summary>누른 단추의 번호. 창 닫기(X)나 Esc면 -1.</summary>
    public int Result { get; private set; } = -1;

    public MessageDialog(string title, string message, IReadOnlyList<string> buttons, int defaultIndex = 0) : this()
    {
        Title = title;
        MessageText.Text = message;
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            var b = new Button { Content = buttons[i], MinWidth = 88, IsDefault = i == defaultIndex };
            if (i == defaultIndex) b.Classes.Add("accent");
            b.Click += (_, _) => { Result = index; Close(); };
            ButtonPanel.Children.Add(b);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }
}
