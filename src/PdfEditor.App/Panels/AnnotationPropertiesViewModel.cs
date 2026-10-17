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
    [ObservableProperty] private bool _isFillMixed;
    [ObservableProperty] private bool _showWidth;
    [ObservableProperty] private float _width = 1f;
    [ObservableProperty] private int _opacityPercent = 100;
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private bool _canEditAuthor;
    [ObservableProperty] private string _createdText = "";
    [ObservableProperty] private string _modifiedText = "";

    // --- M3a: 새 유형 속성 ---
    [ObservableProperty] private bool _showLineEndings;
    [ObservableProperty] private LineEnding _startEnding;
    [ObservableProperty] private LineEnding _endEnding;
    [ObservableProperty] private bool _showCloud;
    [ObservableProperty] private bool _cloudOn;
    [ObservableProperty] private float _cloudIntensity = 1f;
    [ObservableProperty] private bool _showText;
    [ObservableProperty] private float _fontSizePt = 12f;
    [ObservableProperty] private PdfColor? _textColor;
    [ObservableProperty] private TextAlign _align;
    [ObservableProperty] private bool _showStamp;
    [ObservableProperty] private string? _currentStampName;
    [ObservableProperty] private bool _showAttachment;
    [ObservableProperty] private string _attachmentName = "";
    [ObservableProperty] private bool _canSaveAttachment;

    public const float MinFontSize = 6f, MaxFontSize = 72f;
    public const float MinCloud = 0.5f, MaxCloud = 2f;

    /// <summary>선 끝 모양 8가지 (그림 목록 순서).</summary>
    public static IReadOnlyList<LineEnding> LineEndingChoices { get; } = Enum.GetValues<LineEnding>();
    public static string LineEndingName(LineEnding e) => e switch
    {
        LineEnding.None => Strings.LineEnding_None,
        LineEnding.Square => Strings.LineEnding_Square,
        LineEnding.Circle => Strings.LineEnding_Circle,
        LineEnding.Diamond => Strings.LineEnding_Diamond,
        LineEnding.OpenArrow => Strings.LineEnding_OpenArrow,
        LineEnding.ClosedArrow => Strings.LineEnding_ClosedArrow,
        LineEnding.Butt => Strings.LineEnding_Butt,
        _ => Strings.LineEnding_Slash,
    };
    public IReadOnlyList<string> StampNames => DocumentTabViewModel.BuiltinStampNames;
    public static string StampDisplayName(string builtinName) => DocumentTabViewModel.BuiltinStamp(builtinName).Text ?? builtinName;
    public string FontSizeLabel => string.Format(Strings.Prop_Annot_FontSize, FontSizePt);
    public string CloudIntensityLabel => string.Format(Strings.Prop_Annot_CloudIntensity, CloudIntensity);

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
        or ToolKind.Underline or ToolKind.StrikeOut or ToolKind.StickyNote or ToolKind.Rectangle or ToolKind.Ellipse
        or ToolKind.Line or ToolKind.Arrow or ToolKind.PolyLine or ToolKind.Polygon or ToolKind.Cloud
        or ToolKind.TextBox or ToolKind.Typewriter or ToolKind.Callout or ToolKind.InsertText or ToolKind.ReplaceText
        or ToolKind.Stamp;

    // 선 두께·채우기가 의미 있는 유형 (타자기는 테두리·채우기 없음)
    private static bool HasWidth(Annotation a) => a.Kind is AnnotKind.Ink or AnnotKind.Square or AnnotKind.Circle
        or AnnotKind.Line or AnnotKind.PolyLine or AnnotKind.Polygon || IsBoxedFreeText(a);
    private static bool HasFill(Annotation a) => a.Kind is AnnotKind.Square or AnnotKind.Circle or AnnotKind.Polygon || IsBoxedFreeText(a);
    private static bool IsBoxedFreeText(Annotation a) => a.Kind == AnnotKind.FreeText && a.Intent != FreeTextIntent.Typewriter;
    private static bool ToolHasFill(ToolKind t) => t is ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Polygon or ToolKind.Cloud
        or ToolKind.TextBox or ToolKind.Callout;
    private static bool ToolHasWidth(ToolKind t) => t is ToolKind.Pencil or ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Line
        or ToolKind.Arrow or ToolKind.PolyLine or ToolKind.Polygon or ToolKind.Cloud or ToolKind.TextBox or ToolKind.Callout;

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
                ShowFill = sel.All(HasFill);
                int fills = sel.Select(a => a.FillColor).Distinct().Count();
                IsFillMixed = ShowFill && fills > 1;
                Fill = ShowFill && fills == 1 ? sel[0].FillColor : null;
                ShowWidth = sel.All(HasWidth);
                Width = Math.Clamp(sel[0].BorderWidth, MinWidth, MaxWidth);
                OpacityPercent = ClampOpacity((int)MathF.Round(sel[0].Opacity * 100f));
                RefreshKindSpecific(sel);
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
                ShowFill = ToolHasFill(_tab.Tool);
                Fill = ShowFill ? st.Fill : null; IsFillMixed = false;
                ShowWidth = ToolHasWidth(_tab.Tool);
                Width = st.Width;
                OpacityPercent = ClampOpacity((int)MathF.Round(st.Opacity * 100f));
                RefreshKindSpecific([]);
                if (_tab.Tool == ToolKind.Stamp) { ShowStamp = true; CurrentStampName = StampNameOf(_tab.Tools.LastStamp); }
                CanEditAuthor = false; Author = ""; CreatedText = ""; ModifiedText = "";
            }
            else
            {
                RefreshKindSpecific([]);
                Mode = AnnotationPropertiesMode.None;
                KindName = "";
                Color = null; IsColorMixed = false; ShowFill = false; Fill = null; IsFillMixed = false; ShowWidth = false;
                CanEditAuthor = false; Author = ""; CreatedText = ""; ModifiedText = "";
            }
        }
        finally { _syncing = false; }
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(WidthLabel));
        OnPropertyChanged(nameof(OpacityLabel));
        OnPropertyChanged(nameof(FontSizeLabel));
        OnPropertyChanged(nameof(CloudIntensityLabel));
    }

    private AnnotationAssets AssetsNow() => _tab.Tools.Assets?.Invoke() ?? AnnotationAssets.Empty;

    private static string? StampNameOf(StampSpec? s) => s is { Kind: StampKind.Builtin } ? s.BuiltinName : null;

    // 선택에 따라 보이는 유형별 속성 (선 끝·클라우드·글자·스탬프·첨부). sel이 비면 모두 숨긴다.
    private void RefreshKindSpecific(List<Annotation> sel)
    {
        ShowLineEndings = sel.Count > 0 && sel.All(a => a.Kind is AnnotKind.Line or AnnotKind.PolyLine);
        if (ShowLineEndings) { StartEnding = sel[0].StartEnding; EndEnding = sel[0].EndEnding; }
        ShowCloud = sel.Count > 0 && sel.All(a => a.Kind is AnnotKind.Square or AnnotKind.Polygon);
        CloudOn = ShowCloud && sel[0].CloudIntensity > 0f;
        CloudIntensity = ShowCloud && sel[0].CloudIntensity > 0f ? Math.Clamp(sel[0].CloudIntensity, MinCloud, MaxCloud) : 1f;
        ShowText = sel.Count > 0 && sel.All(a => a.Kind == AnnotKind.FreeText);
        if (ShowText)
        {
            FontSizePt = Math.Clamp(sel[0].FontSizePt, MinFontSize, MaxFontSize);
            TextColor = sel[0].TextColor ?? sel[0].Color;
            Align = sel[0].Align;
        }
        ShowStamp = sel.Count == 1 && sel[0].Kind == AnnotKind.Stamp;
        CurrentStampName = ShowStamp ? StampNameOf(sel[0].Stamp) : null;
        bool attach = sel.Count == 1 && sel[0].Kind == AnnotKind.FileAttachment;
        ShowAttachment = attach;
        AttachmentName = attach ? sel[0].AttachmentName ?? "" : "";
        CanSaveAttachment = attach && sel[0].AttachmentId is { } id && AssetsNow().HasFile(id);
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
        AnnotKind.Line => Strings.AnnotKind_Line,
        AnnotKind.PolyLine => Strings.AnnotKind_PolyLine,
        AnnotKind.Polygon => Strings.AnnotKind_Polygon,
        AnnotKind.FreeText => Strings.AnnotKind_FreeText,
        AnnotKind.Caret => Strings.AnnotKind_Caret,
        AnnotKind.Stamp => Strings.AnnotKind_Stamp,
        AnnotKind.FileAttachment => Strings.AnnotKind_FileAttachment,
        _ => "",
    };

    private static string FormatTime(DateTimeOffset? t) =>
        t is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : Strings.Prop_Unknown;

    private static int ClampOpacity(int v) => Math.Clamp((int)Math.Round(v / (double)OpacityStep) * OpacityStep, MinOpacity, MaxOpacity);
    private static float ClampWidth(float v) => Math.Clamp(MathF.Round(v / WidthStep) * WidthStep, MinWidth, MaxWidth);

    // ---- 값 바뀜 (슬라이더·키보드) ----

    // 슬라이더가 만진 속성만 적용한다 (여러 개 선택 때 패널 값은 첫 번째 주석에서 온 대표값이라 나머지를 덮어쓰면 안 된다).
    private bool _widthEdited, _opacityEdited;

    partial void OnWidthChanged(float value)
    {
        OnPropertyChanged(nameof(WidthLabel));
        if (_syncing) return;
        float c = ClampWidth(value);
        if (c != value) { Width = c; return; }
        _widthEdited = true;
        ValueEdited();
    }

    partial void OnOpacityPercentChanged(int value)
    {
        OnPropertyChanged(nameof(OpacityLabel));
        if (_syncing) return;
        int c = ClampOpacity(value);
        if (c != value) { OpacityPercent = c; return; }
        _opacityEdited = true;
        ValueEdited();
    }

    private void ValueEdited()
    {
        if (_previewing) UpdatePreview();
        else _ = CommitSliderSafeAsync();   // 키보드 변경: 바로 커밋
    }

    // 버려진(unobserved) 예외 대신 사용자 안내로 알린다.
    private async Task CommitSliderSafeAsync()
    {
        try { await CommitSliderAsync(); }
        catch (Exception) { _tab.RaiseNotice(Strings.Msg_PropertyEditFailed); }
    }

    // ---- 슬라이더 끌기 ----

    /// <summary>슬라이더 끌기 시작. 이후 값 변경은 Previews만 갱신하고, CommitPreviewAsync에서 명령 하나를 만든다.</summary>
    public void BeginPreview()
    {
        if (_previewing || Mode == AnnotationPropertiesMode.None) return;
        _snapshot = Mode == AnnotationPropertiesMode.Selection ? SelectedNow() : [];
        _widthEdited = _opacityEdited = false;
        _previewing = true;
    }

    private void UpdatePreview()
    {
        if (Mode != AnnotationPropertiesMode.Selection) return;
        var now = _clock();
        Previews = _snapshot.ToDictionary(a => a.Id, a => (Annotation?)Adjust(a, now, _widthEdited, _opacityEdited));
        _tab.RaiseAnnotationPreview(_tab.Selection.Page, Previews);
    }

    private Annotation Adjust(Annotation a, DateTimeOffset now, bool width, bool opacity) => a with
    {
        BorderWidth = width && HasWidth(a) ? Width : a.BorderWidth,
        Opacity = opacity ? OpacityPercent / 100f : a.Opacity,
        ModifiedAt = now,
    };

    private ToolStyle ToolStyleNow() =>
        new(Color ?? DocumentTabViewModel.StyleFor(_tab.Tool).Color, Fill, OpacityPercent / 100f, Width);

    /// <summary>끌기를 놓음. 선택 모드면 명령 하나, 도구 기본값 모드면 설정 저장.</summary>
    public async Task CommitPreviewAsync()
    {
        if (!_previewing) return;
        _previewing = false;
        _snapshot = [];
        Previews = null;
        bool w = _widthEdited, o = _opacityEdited;
        _widthEdited = _opacityEdited = false;
        try
        {
            // 끌기 시작 때의 스냅숏 대신 커밋 때 현재 주석을 다시 찾는다 (그 사이 바뀌거나 사라진 주석 처리).
            if (Mode == AnnotationPropertiesMode.Selection) await ApplyAsync(a => Adjust(a, _clock(), w, o));
            else if (Mode == AnnotationPropertiesMode.ToolDefaults) DocumentTabViewModel.SaveToolStyle(_tab.Tool, ToolStyleNow());
        }
        finally { _tab.RaiseAnnotationPreview(null, null); Refresh(); }
    }

    private async Task CommitSliderAsync()
    {
        _snapshot = [];
        _previewing = true;
        await CommitPreviewAsync();
    }

    // ---- 즉시 편집 ----

    /// <summary>색 견본 클릭.</summary>
    public Task SetColorAsync(PdfColor c) =>
        EditAsync(a => a with { Color = c, ModifiedAt = _clock() }, st => st with { Color = c });

    /// <summary>채우기 견본 클릭 (null = 채우기 없음).</summary>
    public Task SetFillAsync(PdfColor? c) =>
        EditAsync(a => HasFill(a) ? a with { FillColor = c, ModifiedAt = _clock() } : a,
            st => st with { Fill = c });

    // ---- 유형별 속성: 명령 하나 = 바뀐 속성 하나 ----

    /// <summary>선 끝 모양 (시작이면 start=true). 선·폴리라인만 바뀌고 다른 속성은 그대로.</summary>
    public Task SetLineEndingAsync(bool start, LineEnding e) =>
        EditAsync(a => a.Kind is not (AnnotKind.Line or AnnotKind.PolyLine) ? a
            : start ? a with { StartEnding = e, ModifiedAt = _clock() } : a with { EndEnding = e, ModifiedAt = _clock() },
            null);

    /// <summary>클라우드 켜기·끄기 (사각형·다각형). 켜면 세기 1(또는 기존 값), 끄면 0.</summary>
    public Task SetCloudAsync(bool on) =>
        EditAsync(a => a.Kind is not (AnnotKind.Square or AnnotKind.Polygon) ? a
            : a with { CloudIntensity = on ? (a.CloudIntensity > 0f ? a.CloudIntensity : 1f) : 0f, ModifiedAt = _clock() },
            null);

    public Task SetCloudIntensityAsync(float v) =>
        EditAsync(a => a.Kind is not (AnnotKind.Square or AnnotKind.Polygon) || a.CloudIntensity <= 0f ? a
            : a with { CloudIntensity = Math.Clamp(v, MinCloud, MaxCloud), ModifiedAt = _clock() },
            null);

    /// <summary>글자 크기 6~72pt (FreeText). 타자기는 상자를 글에 맞춰 다시 계산한다 (같은 명령 안에서).</summary>
    public Task SetFontSizeAsync(float pt)
    {
        float c = Math.Clamp(pt, MinFontSize, MaxFontSize);
        return EditAsync(a => a.Kind != AnnotKind.FreeText ? a : _tab.Tools.FitForCommit(a with { FontSizePt = c, ModifiedAt = _clock() }), null);
    }

    public Task SetTextColorAsync(PdfColor c) =>
        EditAsync(a => a.Kind != AnnotKind.FreeText ? a : a with { TextColor = c, ModifiedAt = _clock() }, null);

    public Task SetAlignAsync(TextAlign align) =>
        EditAsync(a => a.Kind != AnnotKind.FreeText ? a : _tab.Tools.FitForCommit(a with { Align = align, ModifiedAt = _clock() }), null);

    /// <summary>스탬프 바꾸기. 선택한 스탬프 주석이 있으면 그 주석(명령 하나), Stamp 도구만 켜져 있으면 다음에 놓을 스탬프.</summary>
    public async Task SetStampAsync(StampSpec spec)
    {
        if (Mode == AnnotationPropertiesMode.ToolDefaults && _tab.Tool == ToolKind.Stamp)
        {
            _tab.Tools.LastStamp = spec;
            Refresh();
            return;
        }
        await EditAsync(a => a.Kind != AnnotKind.Stamp ? a
            : a with { Stamp = spec, Contents = DocumentTabViewModel.StampLabelFor(spec), ModifiedAt = _clock() }, null);
    }

    public Task SetStampBuiltinAsync(string name) => SetStampAsync(DocumentTabViewModel.BuiltinStamp(name));

    public Task SetStampTextAsync(string text)
    {
        string t = (text ?? "").Trim();
        return t.Length == 0 ? Task.CompletedTask : SetStampAsync(new StampSpec(StampKind.Text, "", t, null));
    }

    /// <summary>그림 스탬프: 보관소(AddImage)가 PNG·JPEG와 크기를 검증한다. 실패하면 안내만 하고 아무것도 바꾸지 않는다.</summary>
    public async Task SetStampImageAsync(byte[] encoded)
    {
        string id;
        try { id = AssetsNow().AddImage(encoded); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _tab.RaiseNotice(Strings.Msg_ImageAddFailed);
            return;
        }
        await SetStampAsync(new StampSpec(StampKind.Image, "", null, id));
    }

    public void NotifyImageFailed() => _tab.RaiseNotice(Strings.Msg_ImageAddFailed);

    /// <summary>
    /// 첨부 파일 저장: 저장 위치를 pickSavePath(제안 이름)로 묻고 그 위치에만 쓴다. 취소하면 아무것도 하지 않고,
    /// 실패하면 안내만 한다. 제안 이름은 SafeFileName으로 정리한 것이다.
    /// </summary>
    public async Task SaveSelectedAttachmentAsync(Func<string, Task<string?>> pickSavePath)
    {
        try
        {
            if (ReadSelectedAttachment() is not { } file) return;
            if (await pickSavePath(file.SafeName) is not { } target) return;
            await File.WriteAllBytesAsync(target, file.Data);
        }
        catch (Exception) { _tab.RaiseNotice(Strings.Msg_AttachmentSaveFailed); }
    }

    /// <summary>선택한 첨부 주석의 파일 (저장 대화상자용). 안전한 이름으로 정리해 돌려준다. 읽을 수 없으면 null.</summary>
    public (string SafeName, byte[] Data)? ReadSelectedAttachment()
    {
        var sel = SelectedNow();
        if (sel.Count != 1 || sel[0] is not { Kind: AnnotKind.FileAttachment, AttachmentId: { } id }) return null;
        var assets = AssetsNow();
        if (!assets.HasFile(id)) return null;
        var (name, data) = assets.GetFile(id);
        return (Pdf.Native.SafeFileName.From(name), data);
    }

    /// <summary>작성자 입력 확정 (선택이 하나일 때만).</summary>
    public async Task CommitAuthorAsync()
    {
        if (Mode != AnnotationPropertiesMode.Selection || !CanEditAuthor) return;
        string name = Author.Trim();
        await ApplyAsync(a => a with { Author = name, ModifiedAt = _clock() });
        Refresh();
    }

    private async Task EditAsync(Func<Annotation, Annotation> edit, Func<ToolStyle, ToolStyle>? style)
    {
        if (Mode == AnnotationPropertiesMode.Selection) await ApplyAsync(edit);
        else if (Mode == AnnotationPropertiesMode.ToolDefaults && style is not null)
            DocumentTabViewModel.SaveToolStyle(_tab.Tool, style(DocumentTabViewModel.StyleFor(_tab.Tool)));
        Refresh();
    }

    // 원본 주석이 붙을 때까지 기다린 뒤, 지금 선택된 주석의 현재 인스턴스를 찾아 edit를 적용한다 (없어진 주석은 건너뜀).
    private async Task ApplyAsync(Func<Annotation, Annotation> edit)
    {
        if (_tab.Session is null || !_tab.CanAnnotate) return;
        await _tab.Session.AnnotationsReady;
        if (_tab.Selection.Page is not { } page) return;
        var items = new List<(Annotation Before, Annotation After)>();
        foreach (var a in SelectedNow())
        {
            var after = edit(a);
            if (!EqualIgnoringTime(a, after)) items.Add((a, after));
        }
        if (items.Count == 0) return;
        await _tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Replace(page, items));
    }

    private static bool EqualIgnoringTime(Annotation a, Annotation b) => a with { ModifiedAt = null } == b with { ModifiedAt = null };
}
