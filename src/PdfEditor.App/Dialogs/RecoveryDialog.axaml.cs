using Avalonia.Controls;
using PdfEditor.App.Resources;
using PdfEditor.Document.Recovery;

namespace PdfEditor.App.Dialogs;

/// <summary>
/// 시작 시 복구 대화상자. 항목마다 복구/버리기를 고른다(기본: 복구).
/// 확인하면 복구로 고른 SessionId 집합, "나중에"나 창 닫기(X)면 null(아무것도 지우지 않음).
/// </summary>
public sealed partial class RecoveryDialog : Window
{
    private readonly List<(Guid Id, RadioButton Restore)> _rows = [];

    public RecoveryDialog() => InitializeComponent();

    public RecoveryDialog(IReadOnlyList<RecoveryEntry> entries) : this()
    {
        int n = 0;
        foreach (var e in entries)
        {
            string group = "r" + n++;
            var restore = new RadioButton { Content = Strings.Dlg_RecoveryRestore, GroupName = group, IsChecked = true };
            var discard = new RadioButton { Content = Strings.Dlg_RecoveryDiscard, GroupName = group };
            var line = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12 };
            line.Children.Add(restore);
            line.Children.Add(discard);
            var item = new StackPanel { Spacing = 2 };
            var title = new TextBlock { Text = string.Format(Strings.Dlg_RecoveryItem, e.DisplayName, e.SavedAt.LocalDateTime), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            if (e.OriginalPath is not null) ToolTip.SetTip(title, e.OriginalPath);
            item.Children.Add(title);
            item.Children.Add(line);
            ItemsPanel.Children.Add(item);
            _rows.Add((e.SessionId, restore));
        }
        OkButton.Click += (_, _) => Close(_rows.Where(r => r.Restore.IsChecked == true).Select(r => r.Id).ToHashSet());
        LaterButton.Click += (_, _) => Close(null);
    }
}
