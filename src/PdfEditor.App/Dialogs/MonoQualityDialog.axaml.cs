using Avalonia.Controls;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Dialogs;

/// <summary>
/// 흑백 변환 화질(높음·중간·최저) 고르기. 등급마다 저장될 파일의 크기와 목표 이내 여부를 보여 준다.
/// 처음에는 목표 안에 드는 가장 높은 화질이 골라져 있고, 하나도 안 들면 가장 작은 것이 골라져 있다. 확인하면 고른 번호(int), 취소하면 null.
/// </summary>
public sealed partial class MonoQualityDialog : Window
{
    public MonoQualityDialog() => InitializeComponent();

    public MonoQualityDialog(IReadOnlyList<MonoOption> options, long targetBytes) : this()
    {
        long limit = targetBytes + targetBytes / 20;   // SizeReducer와 같은 5% 허용
        int initial = options.Count - 1;
        for (int i = 0; i < options.Count; i++)
            if (options[i].Bytes <= limit) { initial = i; break; }

        var radios = new List<RadioButton>();
        for (int i = 0; i < options.Count; i++)
        {
            var o = options[i];
            string text = string.Format(Strings.Dlg_MonoOption, GradeName(o.Dpi), o.Dpi, ByteSize.Format(o.Bytes),
                o.Bytes <= limit ? Strings.Dlg_MonoFits : Strings.Dlg_MonoOver);
            var r = new RadioButton { Content = text, GroupName = "mono", IsChecked = i == initial };
            radios.Add(r);
            OptionsPanel.Children.Add(r);
        }
        OkButton.Click += (_, _) => Close(Math.Max(0, radios.FindIndex(r => r.IsChecked == true)));
        CancelButton.Click += (_, _) => Close(null);
    }

    /// <summary>단계표의 해상도에 붙인 이름: 600 이상 높음, 300 이상 중간, 그 밑 최저.</summary>
    private static string GradeName(int dpi) => dpi >= 600 ? Strings.Dlg_MonoHigh : dpi >= 300 ? Strings.Dlg_MonoMid : Strings.Dlg_MonoLow;
}
