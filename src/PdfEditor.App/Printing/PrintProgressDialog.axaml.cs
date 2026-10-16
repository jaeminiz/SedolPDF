using Avalonia.Controls;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Printing;

/// <summary>인쇄 진행 표시. 취소 단추나 창 닫기(X)는 취소 요청으로 본다.</summary>
public sealed partial class PrintProgressDialog : Window
{
    private readonly int _total;
    private readonly CancellationTokenSource _cts;
    private bool _finished;

    public PrintProgressDialog() : this(1, new CancellationTokenSource()) { }

    public PrintProgressDialog(int total, CancellationTokenSource cts)
    {
        InitializeComponent();
        _total = Math.Max(1, total);
        _cts = cts;
        Bar.Maximum = _total;
        SetProgress(0);
        CancelButton.Click += (_, _) => { CancelButton.IsEnabled = false; _cts.Cancel(); };
        Closing += (_, e) =>
        {
            if (_finished) return;
            e.Cancel = true;   // 작업이 끝날 때까지 닫지 않고 취소만 요청
            CancelButton.IsEnabled = false;
            _cts.Cancel();
        };
    }

    public void SetProgress(int done)
    {
        Bar.Value = done;
        StatusText.Text = string.Format(Strings.Dlg_PrintProgress, done, _total);
    }

    public void Finish()
    {
        _finished = true;
        Close();
    }
}
