using CommunityToolkit.Mvvm.ComponentModel;

namespace PdfEditor.App.Shell;

/// <summary>왼쪽 패널·속성 패널의 폭 (0.5.0): 끌어서 바꾸고, 더블클릭하면 기본 폭으로 돌아가며, 종류별로 설정에 기억한다.</summary>
public sealed partial class MainWindowViewModel
{
    public const double LeftPanelMinWidth = 160, LeftPanelMaxWidth = 560;
    public const double PropertiesPanelMinWidth = 180, PropertiesPanelMaxWidth = 480, PropertiesPanelDefaultWidth = 220;

    private readonly Dictionary<PanelKind, double> _leftWidths = [];
    private double? _propertiesWidth;

    /// <summary>왼쪽 패널 폭이 바뀌었을 때(끌기·복원) 설정에 저장하라고 알린다.</summary>
    public event EventHandler? PanelWidthsChanged;

    /// <summary>패널 종류별 기본 폭: 코멘트 목록·찾아 바꾸기는 글이 길어 더 넓게.</summary>
    public static double DefaultLeftPanelWidth(PanelKind kind) => kind is PanelKind.Comments ? 320 : kind is PanelKind.Replace ? 340 : 200;

    public double LeftPanelWidth =>
        _leftWidths.TryGetValue(ActiveLeftPanel, out var w) ? w : DefaultLeftPanelWidth(ActiveLeftPanel);

    public double PropertiesPanelWidth => _propertiesWidth ?? PropertiesPanelDefaultWidth;

    public IReadOnlyDictionary<PanelKind, double> LeftPanelWidths => _leftWidths;
    public double? SavedPropertiesPanelWidth => _propertiesWidth;

    /// <summary>현재 왼쪽 패널 종류의 폭을 정한다(최소·최대로 맞춤). 기본 폭과 같으면 기억을 지운다.</summary>
    public void SetLeftPanelWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width)) return;
        width = Math.Round(Math.Clamp(width, LeftPanelMinWidth, LeftPanelMaxWidth));
        if (width == DefaultLeftPanelWidth(ActiveLeftPanel)) _leftWidths.Remove(ActiveLeftPanel);
        else _leftWidths[ActiveLeftPanel] = width;
        OnPropertyChanged(nameof(LeftPanelWidth));
        PanelWidthsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetLeftPanelWidth() => SetLeftPanelWidth(DefaultLeftPanelWidth(ActiveLeftPanel));

    public void SetPropertiesPanelWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width)) return;
        width = Math.Round(Math.Clamp(width, PropertiesPanelMinWidth, PropertiesPanelMaxWidth));
        _propertiesWidth = width == PropertiesPanelDefaultWidth ? null : width;
        OnPropertyChanged(nameof(PropertiesPanelWidth));
        PanelWidthsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetPropertiesPanelWidth() => SetPropertiesPanelWidth(PropertiesPanelDefaultWidth);

    /// <summary>저장된 폭을 되살린다(범위 밖·알 수 없는 값은 무시).</summary>
    public void ApplyPanelWidths(PdfEditor.App.Settings.AppSettings s)
    {
        _leftWidths.Clear();
        if (s.LeftPanelWidths is { } d)
            foreach (var (k, v) in d)
                if (Enum.TryParse<PanelKind>(k, out var kind) && Enum.IsDefined(kind) && double.IsFinite(v))
                    _leftWidths[kind] = Math.Clamp(v, LeftPanelMinWidth, LeftPanelMaxWidth);
        _propertiesWidth = s.PropertiesPanelWidth is { } p && double.IsFinite(p) ? Math.Clamp(p, PropertiesPanelMinWidth, PropertiesPanelMaxWidth) : null;
        OnPropertyChanged(nameof(LeftPanelWidth));
        OnPropertyChanged(nameof(PropertiesPanelWidth));
    }
}
