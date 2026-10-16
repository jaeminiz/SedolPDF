using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Panels;

public enum AnnotationPropertiesMode { None, Selection, ToolDefaults }

/// <summary>
/// 속성 패널의 주석 속성 모델. 선택이 있으면 선택한 주석을 고치고(명령), 만들기 도구만 켜져 있으면
/// 그 도구의 기본 모양을 고친다(설정 저장). 색·채우기·작성자 = 명령 하나, 슬라이더 = Begin/Commit 한 쌍에 명령 하나.
/// 모든 명령은 탭의 RunAnnotationCommandAsync를 거친다 (원본 주석이 붙을 때까지 기다리고 권한을 지킨다).
/// </summary>
public sealed partial class AnnotationPropertiesViewModel : ObservableObject
{
    public const float MinWidth = 0.5f, MaxWidth = 12f, WidthStep = 0.5f;
    public const int MinOpacity = 10, MaxOpacity = 100, OpacityStep = 5;

    private readonly DocumentTabViewModel _tab;
    private readonly Func<DateTimeOffset> _clock;
    private bool _syncing;
    private bool _previewing;
    private List<Annotation> _snapshot = [];

    [ObservableProperty] private AnnotationPropertiesMode _mode;
    [ObservableProperty] private string _kindName = "";
    [ObservableProperty] private PdfColor? _color;
    [ObservableProperty] private bool _isColorMixed;
    [ObservableProperty] private bool _showFill;
    [ObservableProperty] private PdfColor? _fill;
    [ObservableProperty] private bool _showWidth;
    [ObservableProperty] private float _width = 1f;
    [ObservableProperty] private int _opacityPercent = 100;
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private bool _canEditAuthor;
    [ObservableProperty] private string _createdText = "";
    [ObservableProperty] private string _modifiedText = "";

    public AnnotationPropertiesViewModel(DocumentTabViewModel tab, Func<DateTimeOffset>? clock = null)
    {
        _tab = tab;
        _clock = clock ?? (() => DateTimeOffset.Now);
        tab.Selection.Changed += Refresh;
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DocumentTabViewModel.Tool)) Refresh(); };
        if (tab.Session is { } s) s.Annotations.Changed += _ => tab.PostToUi(Refresh);
        Refresh();
    }

    public IReadOnlyList<PdfColor> Swatches => ToolDefaults.Swatches;
    public bool IsActive => Mode != AnnotationPropertiesMode.None;
    public string WidthLabel => string.Format(Strings.Prop_Annot_Width, Width);
    public string OpacityLabel => string.Format(Strings.Prop_Annot_Opacity, OpacityPercent);

    /// <summary>끌기 중인 슬라이더 미리보기(선택 모드). 없으면 null.</summary>
    public IReadOnlyDictionary<AnnotId, Annotation?>? Previews { get; private set; }

    private static bool IsCreateTool(ToolKind t) => t is ToolKind.Pencil or ToolKind.HighlightText or ToolKind.HighlightArea
        or ToolKind.Underline or ToolKind.StrikeOut or ToolKind.StickyNote or ToolKind.Rectangle or ToolKind.Ellipse;

    private List<Annotation> SelectedNow()
    {
        if (_tab.Session is not { } s || _tab.Selection.Page is not { } p) return [];
        var ids = _tab.Selection.Ids;
        return [.. s.Annotations.Get(p).Items.Select(v => v.Data).Where(a => ids.Contains(a.Id))];
    }

    /// <summary>선택·도구·주석이 바뀌면 값을 다시 읽는다 (UI 스레드).</summary>
    public void Refresh()
    {
        if (_previewing) return;   // 끌기 중에는 값이 튀지 않게 둔다
        _syncing = true;
        try
        {
            var sel = SelectedNow();
            if (sel.Count > 0)
            {
                Mode = AnnotationPropertiesMode.Selection;
                KindName = sel.Count == 1 ? KindText(sel[0].Kind) : string.Format(Strings.Prop_Annot_Multiple, sel.Count);
                var colors = sel.Select(a => a.Color).Distinct().ToList();
                Color = colors.Count == 1 ? colors[0] : null;
                IsColorMixed = colors.Count > 1;
                ShowFill = sel.All(a => a.Kind is AnnotKind.Square or AnnotKind.Circle);
                Fill = ShowFill && sel.Select(a => a.FillColor).Distinct().Count() == 1 ? sel[0].FillColor : null;
                ShowWidth = sel.All(a => a.Kind is AnnotKind.Ink or AnnotKind.Square or AnnotKind.Circle);
                Width = Math.Clamp(sel[0].BorderWidth, MinWidth, MaxWidth);
                OpacityPercent = ClampOpacity((int)MathF.Round(sel[0].Opacity * 100f));
                CanEditAuthor = sel.Count == 1;
                Author = sel.Count == 1 ? sel[0].Author ?? "" : "";
                CreatedText = sel.Count == 1 ? FormatTime(sel[0].CreatedAt) : "";
                ModifiedText = sel.Count == 1 ? FormatTime(sel[0].ModifiedAt) : "";
            }
            else if (IsCreateTool(_tab.Tool))
            {
                Mode = AnnotationPropertiesMode.ToolDefaults;
                var st = DocumentTabViewModel.StyleFor(_tab.Tool);
                KindName = DocumentTabViewModel.SubjectFor(_tab.Tool);
                Color = st.Color; IsColorMixed = false;
                ShowFill = _tab.Tool is ToolKind.Rectangle or ToolKind.Ellipse;
                Fill = ShowFill ? st.Fill : null;
                ShowWidth = _tab.Tool is ToolKind.Pencil or ToolKind.Rectangle or ToolKind.Ellipse;
                Width = st.Width;
                OpacityPercent = ClampOpacity((int)MathF.Round(st.Opacity * 100f));
                CanEditAuthor = false; Author = ""; CreatedText = ""; ModifiedText = "";
            }
            else
            {
                Mode = AnnotationPropertiesMode.None;
                KindName = "";
                Color = null; IsColorMixed = false; ShowFill = false; Fill = null; ShowWidth = false;
                CanEditAuthor = false; Author = ""; CreatedText = ""; ModifiedText = "";
            }
        }
        finally { _syncing = false; }
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(WidthLabel));
        OnPropertyChanged(nameof(OpacityLabel));
    }

    private static string KindText(AnnotKind k) => k switch
    {
        AnnotKind.Ink => Strings.AnnotKind_Ink,
        AnnotKind.Highlight => Strings.AnnotKind_Highlight,
        AnnotKind.Underline => Strings.AnnotKind_Underline,
        AnnotKind.StrikeOut => Strings.AnnotKind_StrikeOut,
        AnnotKind.Text => Strings.AnnotKind_Text,
        AnnotKind.Square => Strings.AnnotKind_Square,
        AnnotKind.Circle => Strings.AnnotKind_Circle,
        _ => "",
    };

    private static string FormatTime(DateTimeOffset? t) =>
        t is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : Strings.Prop_Unknown;

    private static int ClampOpacity(int v) => Math.Clamp((int)Math.Round(v / (double)OpacityStep) * OpacityStep, MinOpacity, MaxOpacity);
    private static float ClampWidth(float v) => Math.Clamp(MathF.Round(v / WidthStep) * WidthStep, MinWidth, MaxWidth);

    // ---- 값 바뀜 (슬라이더·키보드) ----

    partial void OnWidthChanged(float value)
    {
        OnPropertyChanged(nameof(WidthLabel));
        if (_syncing) return;
        float c = ClampWidth(value);
        if (c != value) { Width = c; return; }
        ValueEdited();
    }

    partial void OnOpacityPercentChanged(int value)
    {
        OnPropertyChanged(nameof(OpacityLabel));
        if (_syncing) return;
        int c = ClampOpacity(value);
        if (c != value) { OpacityPercent = c; return; }
        ValueEdited();
    }

    private void ValueEdited()
    {
        if (_previewing) UpdatePreview();
        else _ = CommitSliderAsync();   // 키보드 변경: 바로 커밋
    }

    // ---- 슬라이더 끌기 ----

    /// <summary>슬라이더 끌기 시작. 이후 값 변경은 Previews만 갱신하고, CommitPreviewAsync에서 명령 하나를 만든다.</summary>
    public void BeginPreview()
    {
        if (_previewing || Mode == AnnotationPropertiesMode.None) return;
        _snapshot = Mode == AnnotationPropertiesMode.Selection ? SelectedNow() : [];
        _previewing = true;
    }

    private void UpdatePreview()
    {
        if (Mode != AnnotationPropertiesMode.Selection) return;
        var now = _clock();
        Previews = _snapshot.ToDictionary(a => a.Id, a => (Annotation?)Adjust(a, now));
        _tab.RaiseAnnotationPreview(_tab.Selection.Page, Previews);
    }

    private Annotation Adjust(Annotation a, DateTimeOffset now) => a with
    {
        BorderWidth = ShowWidth ? Width : a.BorderWidth,
        Opacity = OpacityPercent / 100f,
        ModifiedAt = now,
    };

    private ToolStyle ToolStyleNow() =>
        new(Color ?? DocumentTabViewModel.StyleFor(_tab.Tool).Color, Fill, OpacityPercent / 100f, Width);

    /// <summary>끌기를 놓음. 선택 모드면 명령 하나, 도구 기본값 모드면 설정 저장.</summary>
    public async Task CommitPreviewAsync()
    {
        if (!_previewing) return;
        _previewing = false;
        var snap = _snapshot;
        _snapshot = [];
        Previews = null;
        try
        {
            if (Mode == AnnotationPropertiesMode.Selection) await ApplyAsync(snap, a => Adjust(a, _clock()));
            else if (Mode == AnnotationPropertiesMode.ToolDefaults) DocumentTabViewModel.SaveToolStyle(_tab.Tool, ToolStyleNow());
        }
        finally { _tab.RaiseAnnotationPreview(null, null); Refresh(); }
    }

    private async Task CommitSliderAsync()
    {
        _snapshot = Mode == AnnotationPropertiesMode.Selection ? SelectedNow() : [];
        _previewing = true;
        await CommitPreviewAsync();
    }

    // ---- 즉시 편집 ----

    /// <summary>색 견본 클릭.</summary>
    public Task SetColorAsync(PdfColor c) =>
        EditAsync(a => a with { Color = c, ModifiedAt = _clock() }, st => st with { Color = c });

    /// <summary>채우기 견본 클릭 (null = 채우기 없음).</summary>
    public Task SetFillAsync(PdfColor? c) =>
        EditAsync(a => a.Kind is AnnotKind.Square or AnnotKind.Circle ? a with { FillColor = c, ModifiedAt = _clock() } : a,
            st => st with { Fill = c });

    /// <summary>작성자 입력 확정 (선택이 하나일 때만).</summary>
    public async Task CommitAuthorAsync()
    {
        if (Mode != AnnotationPropertiesMode.Selection || !CanEditAuthor) return;
        string name = Author.Trim();
        await ApplyAsync(SelectedNow(), a => a with { Author = name, ModifiedAt = _clock() });
        Refresh();
    }

    private async Task EditAsync(Func<Annotation, Annotation> edit, Func<ToolStyle, ToolStyle> style)
    {
        if (Mode == AnnotationPropertiesMode.Selection) await ApplyAsync(SelectedNow(), edit);
        else if (Mode == AnnotationPropertiesMode.ToolDefaults)
            DocumentTabViewModel.SaveToolStyle(_tab.Tool, style(DocumentTabViewModel.StyleFor(_tab.Tool)));
        Refresh();
    }

    private async Task ApplyAsync(List<Annotation> before, Func<Annotation, Annotation> edit)
    {
        if (_tab.Selection.Page is not { } page) return;
        var items = new List<(Annotation Before, Annotation After)>();
        foreach (var a in before)
        {
            var after = edit(a);
            if (!EqualIgnoringTime(a, after)) items.Add((a, after));
        }
        if (items.Count == 0) return;
        await _tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Replace(page, items));
    }

    private static bool EqualIgnoringTime(Annotation a, Annotation b) => a with { ModifiedAt = null } == b with { ModifiedAt = null };
}
