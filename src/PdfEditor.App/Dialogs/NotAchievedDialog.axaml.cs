using Avalonia.Controls;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Optimize;

namespace PdfEditor.App.Dialogs;

/// <summary>
/// 목표를 못 맞췄을 때의 저장 방식 선택: 그 상태로 저장 / 컬러 이미지로 변환 / 도면용(색 유지) 변환 높음·중간·최저. 방식마다 저장될 용량(변환은 미리 잰 예상값)과 목표 이내 여부를 한 화면에 보여 준다.
/// 처음에는 목표 안에 드는 가장 높은 화질의 도면용 변환이 골라져 있고, 하나도 안 들면 "그 상태로 저장"이 골라져 있다. 확인하면 고른 방식, 취소하면 Cancel.
/// </summary>
public sealed partial class NotAchievedDialog : Window
{
    public NotAchievedDialog() => InitializeComponent();

    public NotAchievedDialog(string message, long bestEffortBytes, long targetBytes, ConversionEstimates estimates) : this()
    {
        MessageText.Text = message;
        long limit = targetBytes + targetBytes / 20;   // SizeReducer와 같은 5% 허용
        string Fit(long bytes) => bytes <= limit ? Strings.Dlg_MonoFits : Strings.Dlg_MonoOver;

        var entries = new List<(string Text, NotAchievedDecision Decision, long Bytes)>
        {
            (string.Format(Strings.Dlg_NaKeep, ByteSize.Format(bestEffortBytes), Fit(bestEffortBytes)), new(NotAchievedKind.KeepBestEffort), bestEffortBytes),
            (string.Format(Strings.Dlg_NaColor, ByteSize.Format(estimates.ColorImagesBytes), Fit(estimates.ColorImagesBytes)), new(NotAchievedKind.ColorImages), estimates.ColorImagesBytes),
        };
        for (int i = 0; i < estimates.Layered.Count; i++)
        {
            var g = estimates.Layered[i];
            entries.Add((string.Format(Strings.Dlg_NaLayered, GradeName(g.Dpi), g.Dpi, ByteSize.Format(g.Bytes), Fit(g.Bytes)), new(NotAchievedKind.Layered, i), g.Bytes));
        }

        int initial = 0;   // 그 상태로 저장
        for (int i = 2; i < entries.Count; i++)
            if (entries[i].Bytes <= limit) { initial = i; break; }

        var radios = new List<RadioButton>();
        for (int i = 0; i < entries.Count; i++)
        {
            var r = new RadioButton { Content = entries[i].Text, GroupName = "na", IsChecked = i == initial };
            radios.Add(r);
            OptionsPanel.Children.Add(r);
        }
        OkButton.Click += (_, _) => Close(entries[Math.Max(0, radios.FindIndex(r => r.IsChecked == true))].Decision);
        CancelButton.Click += (_, _) => Close(new NotAchievedDecision(NotAchievedKind.Cancel));
    }

    /// <summary>단계표의 해상도에 붙인 이름: 600 이상 높음, 300 이상 중간, 그 밑 최저.</summary>
    private static string GradeName(int dpi) => dpi >= 600 ? Strings.Dlg_MonoHigh : dpi >= 300 ? Strings.Dlg_MonoMid : Strings.Dlg_MonoLow;
}
