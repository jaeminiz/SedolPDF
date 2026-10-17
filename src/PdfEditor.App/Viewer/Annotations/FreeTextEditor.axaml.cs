using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PdfEditor.App.Viewer.Annotations;

/// <summary>
/// 텍스트 상자·타자기·설명선의 글 편집 칸 (Popup 안에 놓여 상자 위에 겹친다). 여러 줄 입력.
/// Ctrl+Enter = 확정 요청, Esc = 취소 요청. 바깥 클릭(확정)은 Popup의 가벼운 닫기가 맡는다.
/// </summary>
public partial class FreeTextEditor : UserControl
{
    public FreeTextEditor()
    {
        InitializeComponent();
        Box.AddHandler(KeyDownEvent, OnBoxKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public event Action? AcceptRequested;
    public event Action? CancelRequested;

    public string Text => Box.Text ?? "";

    /// <summary>화면 글자 크기(DIP) = FontSizePt × 화면 배율. 자동 줄바꿈은 타자기만 끈다.</summary>
    public void Load(string text, double fontSizeDip, double widthDip, double heightDip, Color foreground, bool wrap, TextAlignment align)
    {
        Box.Text = text;
        Box.FontSize = Math.Max(fontSizeDip, 6);
        Box.Foreground = new SolidColorBrush(foreground);
        Box.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        Box.TextAlignment = align;
        Box.MinWidth = Math.Max(widthDip, 40);
        Box.MinHeight = Math.Max(heightDip, Box.FontSize * 1.4 + 6);
        Box.Width = wrap ? Box.MinWidth : double.NaN;
        Box.CaretIndex = text.Length;
    }

    public void FocusBox() => Box.Focus();

    /// <summary>글자 모양 설정 확인용 (테스트).</summary>
    public double EditorFontSize => Box.FontSize;

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e is { Key: Key.Enter, KeyModifiers: KeyModifiers.Control }) { e.Handled = true; AcceptRequested?.Invoke(); }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; CancelRequested?.Invoke(); }
    }
}
