using Avalonia.Controls;
using Avalonia.Input;

namespace PdfEditor.App.Dialogs;

/// <summary>메시지와 단추 몇 개를 보여 주고 누른 단추의 번호를 돌려주는 대화상자. 창을 닫으면 -1.</summary>
public sealed partial class MessageDialog : Window
{
    public MessageDialog() => InitializeComponent();

    /// <summary>누른 단추의 번호. 창 닫기(X)나 Esc면 -1.</summary>
    public int Result { get; private set; } = -1;

    public MessageDialog(string title, string message, IReadOnlyList<string> buttons, int defaultIndex = 0, IReadOnlyList<Key>? accelerators = null) : this()
    {
        Title = title;
        MessageText.Text = message;
        _accelerators = accelerators;
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            var b = new Button { Content = buttons[i], MinWidth = 88, IsDefault = i == defaultIndex };
            if (i == defaultIndex) b.Classes.Add("accent");
            b.Click += (_, _) => { Result = index; Close(); };
            ButtonPanel.Children.Add(b);
        }
    }

    // 단추별 단축 글쇠(buttons와 같은 순서, Key.None이면 없음). Alt를 함께 눌러도 된다.
    private readonly IReadOnlyList<Key>? _accelerators;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (_accelerators is null) return;
        // 글 상자에 입력 중이면 글쇠는 글자로 쓰인다(지금 대화상자에는 글 상자가 없지만 방어).
        if (e.Source is TextBox || FocusManager?.GetFocusedElement() is TextBox) return;
        // Ctrl·Win과 함께 누른 것은 다른 단축키다. Alt·Shift만 허용.
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) return;
        for (int i = 0; i < _accelerators.Count && i < ButtonPanel.Children.Count; i++)
        {
            if (_accelerators[i] == Key.None || _accelerators[i] != e.Key) continue;
            Result = i;
            e.Handled = true;   // 주 창으로 새지 않게
            Close();
            return;
        }
    }
}
