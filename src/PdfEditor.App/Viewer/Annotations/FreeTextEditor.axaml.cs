using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

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

    /// <summary>한 줄 모드(쪽 객체의 텍스트 편집·추가, M4): Enter = 확정, Esc = 취소. 줄바꿈 없음.</summary>
    public bool SingleLine { get; private set; }

    public void LoadLine(string text, double fontSizeDip, double minWidthDip, Color foreground)
    {
        Load(text, fontSizeDip, minWidthDip, 0, foreground, wrap: false, TextAlignment.Left);
        SingleLine = true;
        Box.AcceptsReturn = false;
        Box.MinHeight = 0;
    }

    /// <summary>입력칸 글을 바꾼다(테스트용).</summary>
    public void SetText(string text) { Box.Text = text; Box.CaretIndex = text.Length; }

    public void FocusBox() => Box.Focus();

    /// <summary>입력칸이 IME 조합 중인 글자(밑줄 표시 중인 글)를 갖고 있는가. 조합 중의 Enter·Esc는 조합을 끝내는 키다.</summary>
    private bool IsComposing()
        => Box.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().Any(p => !string.IsNullOrEmpty(p.PreeditText));

    /// <summary>글자 모양 설정 확인용 (테스트).</summary>
    public double EditorFontSize => Box.FontSize;

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (SingleLine)
        {
            if (e.Key == Key.ImeProcessed || IsComposing()) return;
            if (e is { Key: Key.Enter, KeyModifiers: KeyModifiers.None }) { e.Handled = true; AcceptRequested?.Invoke(); }
            else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; CancelRequested?.Invoke(); }
            return;
        }
        if (e is { Key: Key.Enter, KeyModifiers: KeyModifiers.Control }) { e.Handled = true; AcceptRequested?.Invoke(); }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; CancelRequested?.Invoke(); }
    }
}
