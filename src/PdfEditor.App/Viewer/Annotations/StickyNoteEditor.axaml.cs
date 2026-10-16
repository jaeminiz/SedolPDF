using Avalonia.Controls;
using Avalonia.Input;

namespace PdfEditor.App.Viewer.Annotations;

/// <summary>스티커 메모 편집 창의 내용 (Popup 안에 놓인다). 머리글 + 여러 줄 입력. Ctrl+Enter = 확인, Esc = 취소.</summary>
public partial class StickyNoteEditor : UserControl
{
    public StickyNoteEditor()
    {
        InitializeComponent();
        Box.AddHandler(KeyDownEvent, OnBoxKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }


    /// <summary>Ctrl+Enter: 내용을 확정하고 닫기를 요청한다.</summary>
    public event Action? AcceptRequested;
    /// <summary>Esc: 내용을 버리고 닫기를 요청한다.</summary>
    public event Action? CancelRequested;

    public string Text => Box.Text ?? "";

    public void Load(string header, string text)
    {
        Header.Text = header;
        Box.Text = text;
        Box.CaretIndex = text.Length;
    }

    public void FocusBox() => Box.Focus();

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e is { Key: Key.Enter, KeyModifiers: KeyModifiers.Control }) { e.Handled = true; AcceptRequested?.Invoke(); }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; CancelRequested?.Invoke(); }
    }
}
