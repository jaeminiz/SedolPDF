using Avalonia.Controls;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Pages;

namespace PdfEditor.App.Dialogs;

/// <summary>파일에서 쪽 삽입: 위치와 쪽 범위를 묻는다. 범위가 틀리면 이 창 안에서 알리고 닫지 않는다. 취소하면 null.</summary>
public sealed partial class InsertPagesDialog : Window
{
    public InsertPagesDialog() => InitializeComponent();

    public InsertPagesDialog(string fileName, int sourcePageCount) : this()
    {
        SourceText.Text = string.Format(Strings.Dlg_InsertSource, fileName, sourcePageCount);
        RangeBox.TextChanged += (_, _) => ErrorText.IsVisible = false;
        OkButton.Click += (_, _) =>
        {
            string text = RangeBox.Text ?? "";
            if (!PageRangeParser.TryParse(text, sourcePageCount, out _))
            {
                ErrorText.IsVisible = true;
                return;
            }
            var pos = FirstRadio.IsChecked == true ? InsertPosition.First
                : LastRadio.IsChecked == true ? InsertPosition.Last
                : InsertPosition.AfterCurrent;
            Close(new InsertPagesRequest(pos, text));
        };
        CancelButton.Click += (_, _) => Close(null);
        Opened += (_, _) => RangeBox.Focus();
    }
}
