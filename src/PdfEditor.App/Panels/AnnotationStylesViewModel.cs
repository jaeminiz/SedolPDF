using CommunityToolkit.Mvvm.ComponentModel;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Panels;

/// <summary>
/// 이름 붙인 주석 스타일 (M3b B4)과 "형식" 탭의 대상 표시. 선택이 있으면 선택한 주석에 명령 하나로 적용하고
/// (스타일이 담은 속성 중 그 종류에 맞는 것만), 만들기 도구만 켜져 있으면 그 도구의 기본 모양(설정)에 적용한다.
/// 속성 패널(AnnotationPropertiesViewModel)과 같은 규칙을 쓴다: 지금의 현재 주석을 다시 찾아 바꾸고, 명령은 탭의 RunAnnotationCommandAsync를 거친다.
/// </summary>
public sealed partial class AnnotationStylesViewModel : ObservableObject
{
    private readonly DocumentTabViewModel _tab;
    private readonly AnnotationPropertiesViewModel? _props;
    private readonly Func<DateTimeOffset> _clock;
    private IReadOnlyList<AnnotationStyle> _all;

    [ObservableProperty] private string _newStyleName = "";

    public AnnotationStylesViewModel(DocumentTabViewModel tab, AnnotationPropertiesViewModel? props = null, Func<DateTimeOffset>? clock = null)
    {
        _tab = tab;
        _props = props;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _all = LoadAll();
        tab.Selection.Changed += Refresh;
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DocumentTabViewModel.Tool)) Refresh(); };
    }

    /// <summary>기본 스타일 3개 (설정에 Styles가 없을 때). 이름은 화면 문구(리소스)이고, 한 번 저장되면 그 이름이 데이터가 된다.</summary>
    public static IReadOnlyList<AnnotationStyle> DefaultStyles() =>
    [
        new(Strings.Style_ReviewRed, null, "#E5372D", null, 1f, 1.5f),
        new(Strings.Style_HighlightYellow, nameof(AnnotKind.Highlight), "#FFD60A", null, 0.5f, 1f),
        new(Strings.Style_ApprovedGreen, null, "#1F9D55", null, 1f, 1.5f),
    ];

    private static IReadOnlyList<AnnotationStyle> LoadAll()
    {
        try { return DocumentTabViewModel.Settings?.Load().Styles ?? DefaultStyles(); }
        catch (Exception) { return DefaultStyles(); }
    }

    /// <summary>저장된 스타일 전부 (대상과 상관없이).</summary>
    public IReadOnlyList<AnnotationStyle> All => _all;

    // ---- 대상: 선택 또는 도구 ----

    private List<Annotation> SelectedNow()
    {
        if (_tab.Session is not { } s || _tab.Selection.Page is not { } p) return [];
        var ids = _tab.Selection.Ids;
        return [.. s.Annotations.Get(p).Items.Select(v => v.Data).Where(a => ids.Contains(a.Id))];
    }

    private bool HasSelection => _tab.Selection.Ids.Count > 0;

    /// <summary>"형식" 탭의 대상이 있는가: 만들기 도구가 켜져 있거나 선택이 있다.</summary>
    public bool HasTarget => HasSelection || AnnotationPropertiesViewModel.IsCreateTool(_tab.Tool);

    /// <summary>탭 이름 옆 작은 글씨: "클라우드 도구", "사각형 2개 선택". 대상이 없으면 빈 문자열.</summary>
    public string TargetText
    {
        get
        {
            var sel = HasSelection ? SelectedNow() : [];
            if (sel.Count > 0)
                return sel.Select(a => a.Kind).Distinct().Count() == 1
                    ? string.Format(Strings.Format_TargetSelection, AnnotationPropertiesViewModel.KindText(sel[0].Kind), sel.Count)
                    : string.Format(Strings.Format_TargetMixed, sel.Count);
            return AnnotationPropertiesViewModel.IsCreateTool(_tab.Tool)
                ? string.Format(Strings.Format_TargetTool, DocumentTabViewModel.SubjectFor(_tab.Tool)) : "";
        }
    }

    /// <summary>탭이 지금 이 주석을 고칠 수 있는가 (권한). 꺼진 상태에서는 "형식" 탭 컨트롤을 끈다.</summary>
    public bool CanEdit => _tab.CanAnnotate;

    /// <summary>선 끝·클라우드처럼 저장 때 패치가 필요한 속성을 고칠 수 있는가.</summary>
    public bool CanEditPatchProperties => _tab.CanAnnotate && _tab.CanUsePatchFeatures;

    /// <summary>만들기 도구가 만드는 주석 종류 (정해지지 않으면 null).</summary>
    public static AnnotKind? KindOf(ToolKind t) => t switch
    {
        ToolKind.Pencil => AnnotKind.Ink,
        ToolKind.HighlightText or ToolKind.HighlightArea => AnnotKind.Highlight,
        ToolKind.Underline => AnnotKind.Underline,
        ToolKind.StrikeOut => AnnotKind.StrikeOut,
        ToolKind.StickyNote => AnnotKind.Text,
        ToolKind.Rectangle or ToolKind.Cloud => AnnotKind.Square,
        ToolKind.Ellipse => AnnotKind.Circle,
        ToolKind.Line or ToolKind.Arrow => AnnotKind.Line,
        ToolKind.PolyLine => AnnotKind.PolyLine,
        ToolKind.Polygon => AnnotKind.Polygon,
        ToolKind.TextBox or ToolKind.Typewriter or ToolKind.Callout => AnnotKind.FreeText,
        ToolKind.InsertText or ToolKind.ReplaceText => AnnotKind.Caret,
        ToolKind.Stamp => AnnotKind.Stamp,
        ToolKind.FileAttach => AnnotKind.FileAttachment,
        _ => null,
    };

    /// <summary>지금 선택·도구에 맞는 스타일 (적용 종류가 없으면 모두에게 맞음; 대상이 없으면 전부).</summary>
    public IReadOnlyList<AnnotationStyle> Available
    {
        get
        {
            HashSet<AnnotKind>? kinds = null;
            if (HasSelection) kinds = [.. SelectedNow().Select(a => a.Kind)];
            else if (AnnotationPropertiesViewModel.IsCreateTool(_tab.Tool) && KindOf(_tab.Tool) is { } k) kinds = [k];
            if (kinds is null || kinds.Count == 0) return _all;
            return [.. _all.Where(s => s.AppliesTo is null || (Enum.TryParse<AnnotKind>(s.AppliesTo, out var sk) && kinds.Contains(sk)))];
        }
    }

    /// <summary>선택·도구가 바뀌면 목록과 대상 표시를 다시 읽는다 (UI 스레드).</summary>
    public void Refresh()
    {
        _all = LoadAll();
        NotifyAll();
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(Available));
        OnPropertyChanged(nameof(All));
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanEditPatchProperties));
    }

    // ---- 적용 ----

    private sealed record Resolved(AnnotKind? AppliesTo, PdfColor Color, PdfColor? Fill, float Opacity, float Width,
        float? FontSize, LineEnding? Start, LineEnding? End, float? Cloud, LineStyle? Style = null);

    private static bool TryResolve(AnnotationStyle s, out Resolved r)
    {
        r = null!;
        try
        {
            AnnotKind? applies = null;
            if (s.AppliesTo is { } at)
            {
                if (!Enum.TryParse<AnnotKind>(at, out var k)) return false;
                applies = k;
            }
            LineEnding? Ending(string? name) =>
                name is null ? null : Enum.TryParse<LineEnding>(name, out var e) ? e : throw new FormatException(name);
            r = new Resolved(applies, PdfColor.FromHex(s.ColorHex), s.FillHex is null ? null : PdfColor.FromHex(s.FillHex),
                Math.Clamp(s.Opacity, 0.1f, 1f), Math.Clamp(s.Width, AnnotationPropertiesViewModel.MinWidth, AnnotationPropertiesViewModel.MaxWidth),
                s.FontSizePt is { } fs ? Math.Clamp(fs, AnnotationPropertiesViewModel.MinFontSize, AnnotationPropertiesViewModel.MaxFontSize) : null,
                Ending(s.StartEnding), Ending(s.EndEnding),
                s.CloudIntensity is { } c ? (c <= 0f ? 0f : Math.Clamp(c, AnnotationPropertiesViewModel.MinCloud, AnnotationPropertiesViewModel.MaxCloud)) : null,
                s.LineStyle is { } lsn ? (Enum.TryParse<LineStyle>(lsn, out var lst) ? lst : throw new FormatException(lsn)) : null);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// 스타일 적용. 선택이 있으면 선택한 주석 전부에 명령 하나(종류에 맞는 속성만 바꿈), 없고 만들기 도구가 켜져 있으면 그 도구의 기본 모양.
    /// </summary>
    public async Task ApplyAsync(AnnotationStyle style)
    {
        if (_tab.Session is null || !_tab.CanAnnotate) return;
        if (!TryResolve(style, out var r)) { _tab.RaiseNotice(Strings.Msg_StyleBroken); return; }

        if (HasSelection)
        {
            // 원본 주석이 붙을 때까지 기다린 뒤 지금 선택된 주석의 현재 인스턴스를 찾는다 (그 사이 바뀌거나 사라진 주석 처리).
            await _tab.Session.AnnotationsReady;
            if (_tab.Selection.Page is not { } page) return;
            var now = _clock();
            var items = new List<(Annotation Before, Annotation After)>();
            foreach (var a in SelectedNow())
            {
                var after = ApplyTo(a, r);
                if (after != a) items.Add((a, after with { ModifiedAt = now }));
            }
            if (items.Count == 0) return;
            await _tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Replace(page, items));
        }
        else if (AnnotationPropertiesViewModel.IsCreateTool(_tab.Tool))
        {
            if (r.AppliesTo is { } k && KindOf(_tab.Tool) != k) return;
            var tool = _tab.Tool;
            var cur = DocumentTabViewModel.StyleFor(tool);
            DocumentTabViewModel.SaveToolStyle(tool, cur with
            {
                Color = r.Color,
                Opacity = r.Opacity,
                Fill = r.Fill is not null && AnnotationPropertiesViewModel.ToolHasFill(tool) ? r.Fill : cur.Fill,
                Width = AnnotationPropertiesViewModel.ToolHasWidth(tool) ? r.Width : cur.Width,
            });
            _props?.Refresh();
        }
    }

    // 한 주석에 스타일을 적용한 모양 (시간 도장은 부르는 쪽이 찍는다). 종류에 안 맞는 속성은 건드리지 않는다.
    private Annotation ApplyTo(Annotation a, Resolved r)
    {
        if (r.AppliesTo is { } k && a.Kind != k) return a;
        var b = a with { Color = r.Color, Opacity = r.Opacity };
        if (r.Fill is { } f && AnnotationPropertiesViewModel.HasFill(a)) b = b with { FillColor = f };
        if (AnnotationPropertiesViewModel.HasWidth(a)) b = b with { BorderWidth = r.Width };
        if (a.Kind == AnnotKind.FreeText && r.FontSize is { } fs)
            b = _tab.Tools.FitForCommit(b with { FontSizePt = fs });
        if (_tab.CanUsePatchFeatures)
        {
            if (a.Kind is AnnotKind.Line or AnnotKind.PolyLine)
                b = b with { StartEnding = r.Start ?? b.StartEnding, EndEnding = r.End ?? b.EndEnding };
            if (a.Kind is AnnotKind.Square or AnnotKind.Polygon && r.Cloud is { } c)
                b = b with { CloudIntensity = c };
            if (a.Kind.Applies() && r.Style is { } ls) b = b with { LineStyle = ls };
        }
        return b;
    }

    // ---- 저장·삭제·이름 바꾸기 ----

    private static string? CleanName(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 지금 선택(없으면 도구)의 모양을 이름을 붙여 저장한다. 이름이 비었거나 이미 있으면 거절한다(false, 안내).
    /// 저장하면 기본 스타일을 포함한 전체 목록이 설정에 저장된다. 대상이 없으면 false.
    /// </summary>
    public bool SaveCurrentAs(string name)
    {
        if (CleanName(name) is not { } n || DocumentTabViewModel.Settings is null || CaptureCurrent(n) is not { } style)
        {
            _tab.RaiseNotice(Strings.Msg_StyleNameInvalid);
            return false;
        }
        return Mutate(list =>
        {
            if (list.Any(s => SameName(s.Name, n))) return false;
            list.Add(style);
            return true;
        });
    }

    /// <summary>스타일 이름 입력칸의 이름으로 저장 (성공하면 입력칸을 비운다).</summary>
    public void SaveCurrentFromInput()
    {
        if (SaveCurrentAs(NewStyleName)) NewStyleName = "";
    }

    private AnnotationStyle? CaptureCurrent(string name)
    {
        if (HasSelection)
        {
            var sel = SelectedNow();
            if (sel.Count == 0) return null;
            var a = sel[0];
            bool oneKind = sel.All(x => x.Kind == a.Kind);
            return new AnnotationStyle(name, oneKind ? a.Kind.ToString() : null, (a.Color ?? PdfColor.FromHex("#E5372D")).ToHex(),
                AnnotationPropertiesViewModel.HasFill(a) ? a.FillColor?.ToHex() : null,
                Math.Clamp(a.Opacity, 0.1f, 1f),
                Math.Clamp(a.BorderWidth, AnnotationPropertiesViewModel.MinWidth, AnnotationPropertiesViewModel.MaxWidth),
                a.Kind == AnnotKind.FreeText ? Math.Clamp(a.FontSizePt, AnnotationPropertiesViewModel.MinFontSize, AnnotationPropertiesViewModel.MaxFontSize) : null,
                a.Kind is AnnotKind.Line or AnnotKind.PolyLine ? a.StartEnding.ToString() : null,
                a.Kind is AnnotKind.Line or AnnotKind.PolyLine ? a.EndEnding.ToString() : null,
                a.Kind is AnnotKind.Square or AnnotKind.Polygon ? a.CloudIntensity : null,
                a.Kind.Applies() ? a.LineStyle.ToString() : null);
        }
        if (AnnotationPropertiesViewModel.IsCreateTool(_tab.Tool))
        {
            var st = DocumentTabViewModel.StyleFor(_tab.Tool);
            return new AnnotationStyle(name, KindOf(_tab.Tool)?.ToString(), st.Color.ToHex(),
                AnnotationPropertiesViewModel.ToolHasFill(_tab.Tool) ? st.Fill?.ToHex() : null, st.Opacity, st.Width);
        }
        return null;
    }

    /// <summary>이름으로 스타일을 지운다 (기본 스타일도 지울 수 있다). 없으면 false.</summary>
    public bool Delete(string name) => Mutate(list => list.RemoveAll(s => SameName(s.Name, name)) > 0);

    /// <summary>스타일 이름 바꾸기. 새 이름이 비었거나 다른 스타일과 같으면 거절한다 (대소문자만 바꾸는 것은 허용).</summary>
    public bool Rename(string oldName, string newName)
    {
        if (CleanName(newName) is not { } n) { _tab.RaiseNotice(Strings.Msg_StyleNameInvalid); return false; }
        bool ok = Mutate(list =>
        {
            int i = list.FindIndex(s => SameName(s.Name, oldName));
            if (i < 0 || list.Where((_, j) => j != i).Any(s => SameName(s.Name, n))) return false;
            list[i] = list[i] with { Name = n };
            return true;
        });
        if (!ok) _tab.RaiseNotice(Strings.Msg_StyleNameInvalid);
        return ok;
    }

    // 설정의 읽기-고치기-쓰기를 잠금 하나 안에서 한다 (Update). change가 false를 돌려주면 아무것도 바꾸지 않는다.
    private bool Mutate(Func<List<AnnotationStyle>, bool> change)
    {
        if (DocumentTabViewModel.Settings is not { } store) return false;
        bool changed = false;
        store.Update(cur =>
        {
            var list = (cur.Styles ?? DefaultStyles()).ToList();
            changed = change(list);
            return changed ? cur with { Styles = list } : cur;
        });
        if (changed) Refresh();
        return changed;
    }
}
