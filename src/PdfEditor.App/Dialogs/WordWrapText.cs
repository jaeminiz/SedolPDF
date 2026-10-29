using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PdfEditor.App.Dialogs;

/// <summary>
/// 낱말(공백으로 나뉜 덩어리)을 쪼개지 않고 줄을 바꾸는 글 상자. 기본 TextBlock은 한글을 글자 사이 어디서나 줄바꿈해
/// "기/업"처럼 낱말 중간에서 끊긴다. 낱말마다 TextBlock 하나를 WrapPanel에 넣어 낱말 사이에서만 줄이 바뀐다.
/// 글을 선택해 복사하는 기능은 없다(안내 문구용).
/// </summary>
public sealed class WordWrapText : WrapPanel
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<WordWrapText, string?>(nameof(Text));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<WordWrapText, IBrush?>(nameof(Foreground));
    public static readonly StyledProperty<double> FontSizeProperty = AvaloniaProperty.Register<WordWrapText, double>(nameof(FontSize), 14);

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    static WordWrapText()
    {
        TextProperty.Changed.AddClassHandler<WordWrapText>((c, _) => c.Rebuild());
        ForegroundProperty.Changed.AddClassHandler<WordWrapText>((c, _) => c.Rebuild());
        FontSizeProperty.Changed.AddClassHandler<WordWrapText>((c, _) => c.Rebuild());
    }

    /// <summary>글을 낱말 목록으로 나눈다(연속 공백은 하나로).</summary>
    public static string[] Words(string? text) =>
        (text ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private void Rebuild()
    {
        Children.Clear();
        double gap = Math.Max(3, FontSize * 0.28);
        foreach (string w in Words(Text))
            Children.Add(new TextBlock { Text = w, Foreground = Foreground, FontSize = FontSize, Margin = new Thickness(0, 0, gap, 0), TextWrapping = TextWrapping.NoWrap });
    }
}
