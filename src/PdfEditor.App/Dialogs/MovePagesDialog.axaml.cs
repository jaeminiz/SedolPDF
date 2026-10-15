using Avalonia.Controls;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Dialogs;

/// <summary>쪽 위치 지정 이동: N쪽 앞 / 맨 앞 / 맨 끝. 취소하면 null.</summary>
public sealed partial class MovePagesDialog : Window
{
    public MovePagesDialog() => InitializeComponent();

    public MovePagesDialog(int pageCount, int movingCount) : this()
    {
        DescText.Text = string.Format(Strings.Dlg_MoveDescription, movingCount);
        PageBox.Maximum = pageCount;
        // 숫자 칸을 만지면 "N쪽 앞" 선택으로 본다.
        PageBox.ValueChanged += (_, _) => BeforeRadio.IsChecked = true;
        OkButton.Click += (_, _) =>
        {
            var kind = FirstRadio.IsChecked == true ? MoveTargetKind.First
                : LastRadio.IsChecked == true ? MoveTargetKind.Last
                : MoveTargetKind.BeforePage;
            int page = (int)Math.Clamp(PageBox.Value ?? 1m, 1m, pageCount);
            Close(new MoveTarget(kind, page));
        };
        CancelButton.Click += (_, _) => Close(null);
    }
}
