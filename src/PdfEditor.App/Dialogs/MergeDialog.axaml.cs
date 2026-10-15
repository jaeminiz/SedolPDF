using System.Collections.ObjectModel;
using Avalonia.Controls;

namespace PdfEditor.App.Dialogs;

public sealed record MergeEntry(string Path, string Name);

/// <summary>병합할 파일 목록을 보여 주고 순서 바꾸기·빼기를 받는다. 확인하면 경로 목록, 취소하면 null.</summary>
public sealed partial class MergeDialog : Window
{
    private readonly ObservableCollection<MergeEntry> _entries = [];

    public MergeDialog() => InitializeComponent();

    public MergeDialog(IReadOnlyList<string> paths) : this()
    {
        foreach (var p in paths) _entries.Add(new MergeEntry(p, System.IO.Path.GetFileName(p)));
        FileList.ItemsSource = _entries;
        if (_entries.Count > 0) FileList.SelectedIndex = 0;

        UpButton.Click += (_, _) => Shift(-1);
        DownButton.Click += (_, _) => Shift(1);
        RemoveButton.Click += (_, _) =>
        {
            int i = FileList.SelectedIndex;
            if (i < 0) return;
            _entries.RemoveAt(i);
            if (_entries.Count > 0) FileList.SelectedIndex = Math.Min(i, _entries.Count - 1);
            UpdateButtons();
        };
        FileList.SelectionChanged += (_, _) => UpdateButtons();
        OkButton.Click += (_, _) => Close(_entries.Select(e => e.Path).ToList());
        CancelButton.Click += (_, _) => Close(null);
        UpdateButtons();
    }

    private void Shift(int delta)
    {
        int i = FileList.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _entries.Count) return;
        _entries.Move(i, j);
        FileList.SelectedIndex = j;
    }

    private void UpdateButtons()
    {
        int i = FileList.SelectedIndex;
        UpButton.IsEnabled = i > 0;
        DownButton.IsEnabled = i >= 0 && i < _entries.Count - 1;
        RemoveButton.IsEnabled = i >= 0;
        OkButton.IsEnabled = _entries.Count > 0;
    }
}
