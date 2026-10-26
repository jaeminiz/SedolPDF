using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Viewer.Objects;

/// <summary>
/// 문단 전용 여러 줄 편집 창(M5 C17). Ctrl+Enter = 확정 요청, Esc = 취소 요청, Enter = 줄바꿈(강제 줄바꿈).
/// IME 조합 중에는 모든 키를 조합에 맡긴다(M3b/M4 PreeditText 판정과 같은 방식). 위·아래 줄 포함/제외는 단추와 Shift+↑/↓·Alt+Shift+↑/↓.
/// 실제 확정·폭 계산은 부른 쪽(DocumentView)이 한다.
/// </summary>
public partial class ParagraphEditorPopup : UserControl
{
    private double _dragTotal;
    private bool _loading;
    private bool _canExtend;
    private string _initial = "";

    public ParagraphEditorPopup()
    {
        InitializeComponent();
        AddTopBtn.Content = Strings.Para_AddTop;
        RemoveTopBtn.Content = Strings.Para_RemoveTop;
        AddBottomBtn.Content = Strings.Para_AddBottom;
        RemoveBottomBtn.Content = Strings.Para_RemoveBottom;
        CommitBtn.Content = Strings.Para_Commit;
        CancelBtn.Content = Strings.Para_Cancel;
        foreach (var b in new[] { AddTopBtn, RemoveTopBtn, AddBottomBtn, RemoveBottomBtn }) ToolTip.SetTip(b, Strings.Para_ExtendTip);
        AddTopBtn.Click += (_, _) => RequestExtend(1, 0);
        RemoveTopBtn.Click += (_, _) => RequestExtend(-1, 0);
        AddBottomBtn.Click += (_, _) => RequestExtend(0, 1);
        RemoveBottomBtn.Click += (_, _) => RequestExtend(0, -1);
        CommitBtn.Click += (_, _) => AcceptRequested?.Invoke();
        CancelBtn.Click += (_, _) => CancelRequested?.Invoke();
        Box.AddHandler(KeyDownEvent, OnBoxKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Box.TextChanged += (_, _) => { if (!_loading) TextEdited?.Invoke(); UpdateExtendButtons(); };
        foreach (var (g, leading) in new[] { (LeftGrip, true), (RightGrip, false) })
        {
            g.DragStarted += (_, _) => _dragTotal = 0;
            g.DragDelta += (_, e) =>
            {
                _dragTotal += e.Vector.X;
                Box.Width = Math.Max(40, Box.Width + (leading ? -e.Vector.X : e.Vector.X));   // 끄는 동안은 모양만(놓을 때 줄바꿈을 다시 맞춘다, C13)
            };
            g.DragCompleted += (_, _) => { double d = _dragTotal; _dragTotal = 0; WidthDragged?.Invoke(d, leading); };
        }
    }

    public event Action? AcceptRequested;
    public event Action? CancelRequested;
    /// <summary>글이 사용자 입력으로 바뀌었다.</summary>
    public event Action? TextEdited;
    /// <summary>위·아래 줄 포함(+)/제외(−) 요청 (topDelta, bottomDelta).</summary>
    public event Action<int, int>? ExtendRequested;
    /// <summary>폭 손잡이를 끌어 놓았다: (끈 양(DIP, 오른쪽이 +), 왼쪽 손잡이인가).</summary>
    public event Action<double, bool>? WidthDragged;

    public string Text => Box.Text ?? "";

    /// <summary>처음 글과 달라졌는가(달라졌으면 줄 포함/제외를 막는다 — 직접 고친 글이 사라지지 않게).</summary>
    public bool Modified => !string.Equals(Text.Replace("\r\n", "\n"), _initial.Replace("\r\n", "\n"), StringComparison.Ordinal);

    public double EditorFontSize => Box.FontSize;
    public double BoxWidth => Box.Width;
    public string StatusText => Status.Text ?? "";
    public bool CommitEnabled => CommitBtn.IsEnabled;

    /// <summary>화면 글자 크기(DIP) = 글자 크기(pt) × 화면 배율. widthDip는 문단 상자 폭.</summary>
    public void Load(string text, double fontSizeDip, double widthDip, double minHeightDip, bool canExtend)
    {
        _loading = true;
        try
        {
            _initial = text;
            Box.Text = text;
            Box.FontSize = Math.Max(fontSizeDip, 6);
            Box.Foreground = Brushes.Black;
            Box.Width = Math.Max(widthDip, 60);
            Box.MinHeight = Math.Max(minHeightDip, Box.FontSize * 1.4 + 6);
            Box.CaretIndex = text.Length;
            _canExtend = canExtend;
        }
        finally { _loading = false; }
        UpdateExtendButtons();
    }

    public void SetFontSize(double fontSizeDip) => Box.FontSize = Math.Max(fontSizeDip, 6);

    /// <summary>폭만 바꾼다(손잡이를 놓은 뒤 / 속성 패널의 숫자 칸).</summary>
    public void SetWidth(double widthDip) => Box.Width = Math.Max(widthDip, 60);

    public void SetStatus(string text, ParagraphBoxState state, bool canCommit)
    {
        Status.Text = text;
        Status.Foreground = new SolidColorBrush(Color.Parse(state switch { ParagraphBoxState.Blocked => "#C42B1C", ParagraphBoxState.Overlap => "#B45F06", _ => "#5C5C5C" }));
        CommitBtn.IsEnabled = canCommit;
    }

    public void SetText(string text) { Box.Text = text; Box.CaretIndex = text.Length; }
    public void FocusBox() => Box.Focus();

    private void UpdateExtendButtons()
    {
        bool on = _canExtend && !Modified;
        ExtendRow.IsVisible = _canExtend;
        foreach (var b in new[] { AddTopBtn, RemoveTopBtn, AddBottomBtn, RemoveBottomBtn }) b.IsEnabled = on;
    }

    /// <summary>줄 포함/제외 단추를 누른 것과 같다(시험·단축키).</summary>
    public void RequestExtend(int top, int bottom)
    {
        if (_canExtend && !Modified) ExtendRequested?.Invoke(top, bottom);
    }

    /// <summary>폭 손잡이를 끌었다 놓은 것과 같다(시험).</summary>
    public void SimulateWidthDrag(double deltaDip, bool leading)
    {
        Box.Width = Math.Max(40, Box.Width + (leading ? -deltaDip : deltaDip));
        WidthDragged?.Invoke(deltaDip, leading);
    }

    /// <summary>입력칸이 IME 조합 중인 글자(밑줄 표시 중인 글)를 갖고 있는가. 조합 중의 Enter·Esc는 조합을 끝내는 키다.</summary>
    private bool IsComposing()
        => Box.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().Any(p => !string.IsNullOrEmpty(p.PreeditText));

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed || IsComposing()) return;   // 조합 중: Enter/Esc는 조합만 끝낸다
        if (e is { Key: Key.Enter, KeyModifiers: KeyModifiers.Control }) { e.Handled = true; AcceptRequested?.Invoke(); return; }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; CancelRequested?.Invoke(); return; }
        if (e.Key is Key.Up or Key.Down && (e.KeyModifiers & KeyModifiers.Shift) != 0 && (e.KeyModifiers & ~(KeyModifiers.Shift | KeyModifiers.Alt)) == 0
            && _canExtend && !Modified)
        {
            bool remove = (e.KeyModifiers & KeyModifiers.Alt) != 0, up = e.Key == Key.Up;
            int d = remove ? -1 : 1;
            e.Handled = true;
            ExtendRequested?.Invoke(up ? d : 0, up ? 0 : d);
        }
    }
}
