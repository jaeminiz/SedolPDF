using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;
using PdfEditor.App.Shell;
using PdfEditor.Appearance;
using PdfEditor.App.Viewer;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Saving;

namespace PdfEditor.App.Documents;

public enum ToolKind
{
    Hand, TextSelect, AnnotSelect, Pencil, Eraser, HighlightText, HighlightArea,
    Underline, StrikeOut, StickyNote, Rectangle, Ellipse,
    // M3a
    Line, Arrow, PolyLine, Polygon, Cloud, TextBox, Typewriter, Callout,
    InsertText, ReplaceText, Stamp, FileAttach,
    // M4 (편집 탭): 쪽 객체
    EditObject, EditText, AddText, AddImage, AddShape, AddEllipse, AddLine,
    // 홈 탭(2026-10-20 사용자 요청): 끌어서 고른 영역을 그림으로 클립보드에
    Snapshot,
}

/// <summary>문서 탭 하나. 세션이 있으면 변경 표시(제목 끝 " •")와 쪽 수를 세션에서 따라간다.</summary>
public sealed partial class DocumentTabViewModel : ObservableObject
{
    private const double PtToMm = 25.4 / 72.0;
    private readonly Action<Action> _post;
    private string _baseTitle;

    [ObservableProperty] private double _scale = 1.0;
    [ObservableProperty] private int _currentPageIndex;
    [ObservableProperty] private int _pageCount;
    [ObservableProperty] private double _pageWidthMm;
    [ObservableProperty] private double _pageHeightMm;
    [ObservableProperty] private ViewMode _viewMode = ViewMode.Continuous;
    [ObservableProperty] private ToolKind _tool = ToolKind.Hand;
    [ObservableProperty] private IReadOnlySet<PageId> _selectedPages = new HashSet<PageId>();

    /// <summary>세션 없이 제목만 가진 탭(테스트·자리 표시용).</summary>
    public DocumentTabViewModel(string title) : this(null, title, null) { }

    /// <param name="post">세션 이벤트(전용 스레드에서 발생)를 UI 스레드로 넘기는 함수. null이면 그 자리에서 실행.</param>
    public DocumentTabViewModel(DocumentSession? session, string title, Action<Action>? post)
    {
        Session = session;
        _baseTitle = title;
        _post = post ?? (a => a());
        InitAnnotations();
        if (session is not null)
        {
            ApplyPages();
            session.Commands.Changed += OnSessionChanged;
            session.PagesChanged += OnPagesChanged;
        }
    }

    // ---- 주석 (M2) ----

    /// <summary>
    /// 설정 저장소(앱이 시작할 때 한 번 지정). 작성자 이름과 도구별 기억 스타일을 여기서 읽고 쓴다.
    /// null이면 설정 없이 동작한다 (작성자 = Environment.UserName, 스타일 = ToolDefaults).
    /// </summary>
    public static SettingsStore? Settings { get; set; }

    /// <summary>
    /// 주석 모양 부품(글꼴·그림 보관소). 앱이 시작할 때 한 번 지정한다. 그림 스탬프·첨부 바이트(Assets)는 이 하나의 인스턴스를
    /// 화면·저장·복구·인쇄가 함께 쓴다. null이면(테스트) 빈 보관소를 쓰고 글자가 필요한 도구는 실패한다.
    /// </summary>
    public static AppearanceServices? Appearance { get; set; }

    /// <summary>기본 스탬프 6종의 PDF 이름 (고르는 순서).</summary>
    public static IReadOnlyList<string> BuiltinStampNames { get; } = ["Approved", "Reviewed", "Rejected", "Confidential", "Draft", "Done"];

    private static string BuiltinStampLabel(string name) => name switch
    {
        "Approved" => Strings.Stamp_Approved,
        "Reviewed" => Strings.Stamp_Reviewed,
        "Rejected" => Strings.Stamp_Rejected,
        "Confidential" => Strings.Stamp_Confidential,
        "Draft" => Strings.Stamp_Draft,
        "Done" => Strings.Stamp_Done,
        _ => name,
    };

    /// <summary>기본 스탬프: 표시 문구(한국어)를 Text에 넣어 Painter가 그 글자를 그리게 한다.</summary>
    public static StampSpec BuiltinStamp(string name) => new(StampKind.Builtin, name, BuiltinStampLabel(name), null);

    /// <summary>스탬프의 목록·/Contents용 이름.</summary>
    public static string StampLabelFor(StampSpec s) => s.Kind switch
    {
        StampKind.Builtin => BuiltinStampLabel(s.BuiltinName),
        StampKind.Text => s.Text ?? "",
        _ => Strings.AnnotKind_Stamp,
    };

    private static AppSettings LoadSettings()
    {
        try { return Settings?.Load() ?? AppSettings.Default; } catch (Exception) { return AppSettings.Default; }
    }

    /// <summary>주석 작성자 이름: 설정의 작성자, 없으면 Environment.UserName.</summary>
    public static string CurrentAuthor => LoadSettings().EffectiveAuthor;

    /// <summary>이 도구로 만들 주석의 모양: 설정에 저장된 값, 없으면 기본값.</summary>
    public static ToolStyle StyleFor(ToolKind tool)
    {
        try
        {
            if (LoadSettings().ToolStyles is { } d && d.TryGetValue(tool.ToString(), out var s) && s is not null)
                return new ToolStyle(PdfColor.FromHex(s.ColorHex), s.FillHex is null ? null : PdfColor.FromHex(s.FillHex),
                    Math.Clamp(s.Opacity, 0.1f, 1f), Math.Clamp(s.Width, 0.5f, 12f));
        }
        catch (Exception) { }   // 손상된 항목(null·잘못된 색 등)은 기본값으로
        return ToolDefaults.For(tool);
    }

    /// <summary>이 도구의 모양을 설정에 기억한다.</summary>
    public static void SaveToolStyle(ToolKind tool, ToolStyle style)
    {
        if (Settings is not { } store) return;
        store.Update(cur =>
        {
            var map = cur.ToolStyles is null
                ? new Dictionary<string, ToolStyleSetting>()
                : new Dictionary<string, ToolStyleSetting>(cur.ToolStyles.Where(kv => kv.Value is not null));
            map[tool.ToString()] = new ToolStyleSetting(style.Color.ToHex(), style.Fill?.ToHex(), style.Opacity, style.Width);
            return cur with { ToolStyles = map };
        });
    }

    /// <summary>UI 스레드로 넘기는 함수 (패널 모델이 전용 스레드 이벤트를 받을 때 쓴다).</summary>
    public void PostToUi(Action a) => _post(a);

    /// <summary>속성 패널의 슬라이더 미리보기를 문서 화면에 알린다 (null이면 미리보기 끝).</summary>
    public event Action<PageId?, IReadOnlyDictionary<AnnotId, Annotation?>?>? AnnotationPreviewRequested;
    public void RaiseAnnotationPreview(PageId? page, IReadOnlyDictionary<AnnotId, Annotation?>? items)
        => AnnotationPreviewRequested?.Invoke(page, items);

    private long _fallbackAnnotId;

    /// <summary>선택된 주석 (UI 스레드 전용).</summary>
    public AnnotationSelection Selection { get; } = new();

    /// <summary>만들기·지우개 도구 로직.</summary>
    public AnnotationToolController Tools { get; private set; } = null!;

    /// <summary>선택·이동·크기 조절 로직.</summary>
    public SelectionController Editing { get; } = new(() => DateTimeOffset.Now);

    /// <summary>이 문서에 주석을 더하거나 고칠 수 있는가 (암호 문서의 권한 비트).</summary>
    public bool CanAnnotate => Session is { } s && s.Permissions.CanAnnotate;

    /// <summary>주석 관련 안내 문구(권한 없음 / 원본 주석을 읽지 못함). 없으면 null.</summary>
    public string? AnnotationNotice
    {
        get
        {
            if (Session is null) return null;
            string? notice = !CanAnnotate ? Strings.Msg_AnnotationsRestricted
                : !Session.OriginalAnnotationsEditable ? Strings.Msg_OriginalAnnotationsReadOnly
                : null;
            // 암호 문서는 PDFsharp 패치 단계를 건너뛰므로 패치가 필요한 도구를 쓸 수 없다 (A2).
            if (CanAnnotate && !CanUsePatchFeatures)
                notice = notice is null ? Strings.Msg_PatchFeaturesUnavailableEncrypted
                    : notice + " " + Strings.Msg_PatchFeaturesUnavailableEncrypted;
            return notice;
        }
    }

    /// <summary>선·텍스트 상자·스탬프처럼 저장 때 PDFsharp 패치가 필요한 주석을 이 문서에 넣을 수 있는가 (암호 문서는 불가).</summary>
    public bool CanUsePatchFeatures => Session is { } s && SavePolicy.CanPatch(s);

    public bool HasAnnotationNotice => AnnotationNotice is not null;

    private void InitAnnotations()
    {
        Tools = new AnnotationToolController(
            Session is { } s ? s.Annotations.NewId : () => new AnnotId(Interlocked.Increment(ref _fallbackAnnotId)),
            () => DateTimeOffset.Now, () => CurrentAuthor, SubjectFor);
        // 도구 컨트롤러 연결 (연결하지 않은 항목은 실패하거나 도구가 꺼진다 — 반드시 여기서 모두 지정한다).
        Tools.CanUsePatchFeatures = () => CanUsePatchFeatures;
        Tools.Assets = () => Appearance?.Assets ?? AnnotationAssets.Empty;
        Tools.NewName = () => Guid.NewGuid().ToString("N");
        Tools.FitTypewriter = a => AnnotationPainter.FitTypewriter(a, new PaintContext(Appearance?.Font, Appearance?.Assets ?? AnnotationAssets.Empty));
        Tools.StampLabel = StampLabelFor;
        Tools.LastStamp = BuiltinStamp("Approved");
        // 글자 경계 찾기는 PDFium 스레드 작업이라 동기 함수로 줄 수 없다: 텍스트 삽입은 DocumentView가 비동기로 찾아 CreateInsertCaret을 직접 부른다.
        Tools.CharBoundaryAt = static (_, _, _) => null;
        if (Session is null) return;
        if (Appearance?.Assets is { } sharedAssets) Session.Assets = sharedAssets;   // 쪽 객체 이미지(M4)도 화면·저장·복구·인쇄가 같은 보관소를 읽는다
        Tools.PageAnnotationSource = id => [.. Session.Annotations.Get(id).Items.Select(v => v.Data)];
        Session.Annotations.Changed += OnAnnotationsChanged;
        _ = RefreshAnnotationNoticeAsync();
    }

    public static string SubjectFor(ToolKind t) => t switch
    {
        ToolKind.Pencil => Strings.AnnotKind_Ink,
        ToolKind.HighlightText or ToolKind.HighlightArea => Strings.AnnotKind_Highlight,
        ToolKind.Underline => Strings.AnnotKind_Underline,
        ToolKind.StrikeOut => Strings.AnnotKind_StrikeOut,
        ToolKind.StickyNote => Strings.AnnotKind_Text,
        ToolKind.Rectangle => Strings.AnnotKind_Square,
        ToolKind.Ellipse => Strings.AnnotKind_Circle,
        ToolKind.Line => Strings.AnnotKind_Line,
        ToolKind.Arrow => Strings.AnnotKind_Arrow,
        ToolKind.PolyLine => Strings.AnnotKind_PolyLine,
        ToolKind.Polygon => Strings.AnnotKind_Polygon,
        ToolKind.Cloud => Strings.AnnotKind_Cloud,
        ToolKind.TextBox => Strings.AnnotKind_FreeText,
        ToolKind.Typewriter => Strings.AnnotKind_Typewriter,
        ToolKind.Callout => Strings.AnnotKind_Callout,
        ToolKind.InsertText or ToolKind.ReplaceText => Strings.AnnotKind_Caret,
        ToolKind.Stamp => Strings.AnnotKind_Stamp,
        ToolKind.FileAttach => Strings.AnnotKind_FileAttachment,
        _ => "",
    };

    // 원본 주석을 다 읽은 뒤에야 "읽을 수 없음" 여부가 정해진다.
    private async Task RefreshAnnotationNoticeAsync()
    {
        try { await Session!.AnnotationsReady; } catch (Exception) { }
        _post(() => { OnPropertyChanged(nameof(AnnotationNotice)); OnPropertyChanged(nameof(HasAnnotationNotice)); });
    }

    // 실행 취소 등으로 없어진 주석을 선택에서 뺀다 (전용 스레드에서 오므로 UI 스레드로 넘긴다).
    private void OnAnnotationsChanged(PdfEditor.Document.Annotations.AnnotationsChangedEventArgs e) => _post(() =>
    {
        if (Session is { } s && Selection.Page is { } p && e.Pages.Contains(p)) Selection.Prune(s.Annotations.Get(p));
    });


    // ---- 쪽 객체 편집 (M4) ----

    /// <summary>쪽 객체 선택·옮기기·크기 바꾸기(UI 스레드 전용).</summary>
    public PdfEditor.App.Viewer.Objects.ObjectSelectionController Objects { get; } = new();

    /// <summary>고른 객체의 속성 패널 모델(UI 스레드 전용). 처음 쓸 때 만든다.</summary>
    public PdfEditor.App.Panels.ObjectPropertiesViewModel ObjectProps => _objectProps ??= new(this);
    private PdfEditor.App.Panels.ObjectPropertiesViewModel? _objectProps;

    /// <summary>이 문서의 쪽 내용을 고칠 수 있는가(권한의 "내용 수정" 비트).</summary>
    public bool CanEditContent => Session is { } s && s.Permissions.CanModify;

    /// <summary>앱 글꼴이 필요한 편집(새 텍스트, 원래 글꼴로 안 되는 글 바꾸기)을 할 수 있는가(암호 문서는 패치 단계가 없어 안 됨, M3a A2).</summary>
    public bool CanUseAppFontText => Session is { } s && PdfEditor.Document.Saving.SavePolicy.CanPatch(s);

    /// <summary>쪽 객체 편집 명령을 실행한다. 권한이 없으면 아무것도 하지 않는다.</summary>
    public async Task RunContentCommandAsync(PdfEditor.Document.Content.ContentEditCommand c)
    {
        if (Session is null || !CanEditContent) return;
        await Session.Commands.ExecuteAsync(c);
    }
    /// <summary>문단 편집 명령(M5)을 실행한다. 권한이 없으면 아무것도 하지 않는다.</summary>
    public async Task RunParagraphCommandAsync(PdfEditor.Document.Content.ParagraphEditCommand c)
    {
        if (Session is null || !CanEditContent) return;
        await Session.Commands.ExecuteAsync(c);
    }

    /// <summary>"한 줄만" 설정(편집 탭, M5 C1): 켜면 텍스트 편집 도구가 문단으로 묶지 않고 눌러 고른 한 줄만 고친다(M4 동작). 메인 창이 활성 문서에 맞춰 준다.</summary>
    public bool OneLineOnly { get; set; }

    /// <summary>열려 있는 문단 편집 창의 상자 정보(속성 패널의 폭 칸과 주고받는다, UI 스레드 전용).</summary>
    public PdfEditor.App.Viewer.Objects.ParagraphBoxBridge ParagraphBox { get; } = new();

    /// <summary>
    /// 이미지 추가(편집 탭): 지금 보는 쪽 가운데에 넣고 바로 객체 편집 도구로 선택한다.
    /// 실패하면 안내(NoticeRequested)만 하고 아무것도 바꾸지 않는다.
    /// </summary>
    public async Task AddImageAsync(byte[] encoded)
    {
        if (!CanEditContent || !TryCurrentPage(out var page)) return;
        var r = await PdfEditor.App.Viewer.Objects.ObjectToolController.AddImageAsync(this, page.Id, encoded);
        if (r.Notice is not null) { RaiseNotice(r.Notice); return; }
        if (r.Created is { } created && r.CreatedBounds is { } b)
        {
            Tool = ToolKind.EditObject;
            Objects.Select(new PdfEditor.App.Viewer.Objects.SelectedObject(page.Id, created, r.CreatedType ?? Pdf.Native.PdfPageObjectType.Image, b));
        }
    }

    /// <summary>주석 명령 실행. 원본 주석이 붙을 때까지 기다린 뒤 실행한다. 권한이 없으면 아무것도 하지 않는다.</summary>
    public async Task RunAnnotationCommandAsync(PdfEditor.Document.Commands.IUndoableCommand c)
    {
        if (Session is null || !CanAnnotate) return;
        await Session.AnnotationsReady;
        await Session.Commands.ExecuteAsync(c);
    }

    /// <summary>Esc: 진행 중인 동작을 취소하고, 주석 도구면 주석 선택 도구로 돌아간다 (이미 그 도구면 선택 해제).</summary>
    public void EscapePressed()
    {
        Tools.Cancel();
        Tools.CancelAttach();
        Editing.Cancel();
        if (Tool is ToolKind.Hand or ToolKind.TextSelect) return;
        if (Tool == ToolKind.AnnotSelect) Selection.Clear();
        else Tool = ToolKind.AnnotSelect;
    }

    partial void OnToolChanged(ToolKind value)
    {
        if (value is not (ToolKind.EditObject or ToolKind.EditText or ToolKind.AnnotSelect)) Objects.Clear();   // 객체 선택은 객체 도구·주석 선택 도구에서만(주석 선택에서는 주석과 함께 골라 옮기고 지운다)
        Tools.Cancel();
        Tools.CancelAttach();
        Editing.Cancel();
        if (value != ToolKind.AnnotSelect) Selection.Clear();
    }

    public DocumentSession? Session { get; }

    /// <summary>쪽 수정 권한이 없는 문서(위쪽 안내 막대를 보인다).</summary>
    public bool IsEditRestricted => Session is { } s && !s.Permissions.CanEditPages;

    /// <summary>쪽 편집 명령과 썸네일 목록. 탭을 화면에 올릴 때 MainWindowViewModel이 만든다.</summary>
    public PageCommandsViewModel? PageCommands { get; set; }
    public PdfEditor.App.Panels.ThumbnailsViewModel? Thumbnails { get; set; }

    /// <summary>읽기 전용 패널 모델들(책갈피, 첨부, 레이어, 속성). 각자 처음 열 때 한 번 읽는다.</summary>
    public PdfEditor.App.Panels.BookmarksViewModel? Bookmarks { get; set; }
    public PdfEditor.App.Panels.AttachmentsViewModel? Attachments { get; set; }
    public PdfEditor.App.Panels.LayersViewModel? Layers { get; set; }
    public PdfEditor.App.Panels.PropertiesViewModel? Properties { get; set; }

    /// <summary>코멘트 목록 모델(M3b). 패널을 열 때 만든다.</summary>
    public PdfEditor.App.Panels.CommentsViewModel? Comments { get; set; }

    /// <summary>주석 스타일과 "형식" 탭의 대상 (M3b). 속성 패널의 주석 속성 모델과 같은 상태를 쓴다.</summary>
    public PdfEditor.App.Panels.AnnotationStylesViewModel? Styles { get; set; }

    /// <summary>쪽의 이 사각형(PDF 사용자 좌표)이 화면 안에 보이도록 스크롤해 달라는 요청. 문서 화면이 받는다.</summary>
    public event Action<PageId, Pdf.Native.PdfRectF>? RevealRequested;
    public void RequestReveal(PageId page, Pdf.Native.PdfRectF rect) => RevealRequested?.Invoke(page, rect);

    /// <summary>검색 패널 모델. 문서 화면이 현재 결과(CurrentHit)를 읽어 강조한다.</summary>
    public PdfEditor.App.Panels.SearchViewModel? Search { get; set; }
    /// <summary>찾아 바꾸기 패널 모델(0.4.0, UI 스레드 전용). 탭을 닫을 때 해제한다.</summary>
    public PdfEditor.App.Panels.FindReplaceViewModel? Replace { get; set; }

    /// <summary>텍스트 선택 도구의 선택 상태(한 쪽 안에서만).</summary>
    public TextSelectionModel TextSelection { get; } = new();

    /// <summary>화면이 사용자에게 알려야 할 짧은 안내(예: 복사 제한). 메인 창이 대화상자로 보여 준다.</summary>
    public event Action<string>? NoticeRequested;
    public void RaiseNotice(string message) => NoticeRequested?.Invoke(message);

    /// <summary>화면(우클릭 메뉴)이 선택한 글로 찾기를 요청한다. 메인 창이 검색 패널을 열고 찾는다.</summary>
    public event Action<string>? FindTextRequested;
    public void RequestFind(string text) => FindTextRequested?.Invoke(text);

    /// <summary>탭을 닫을 때(Detach) 발생. 화면이 이 탭의 캐시를 정리하는 데 쓴다.</summary>
    public event Action? Detached;

    /// <summary>문서 화면이 보고하는 보이는 영역 크기(맞춤 배율 계산용).</summary>
    public Size ViewportSize { get; set; }

    /// <summary>페이지 왼쪽 위 기준 커서 위치(mm). 페이지 밖이면 null.</summary>
    [ObservableProperty] private double? _cursorXmm;
    [ObservableProperty] private double? _cursorYmm;

    public void SetCursorFromPagePoint(Point? inPage, double scale)
    {
        if (inPage is not { } p || scale <= 0) { CursorXmm = null; CursorYmm = null; return; }
        CursorXmm = p.X / scale * PtToMm;
        CursorYmm = p.Y / scale * PtToMm;
    }

    public void ZoomIn() => Scale = ZoomMath.Step(Scale, 1);
    public void ZoomOut() => Scale = ZoomMath.Step(Scale, -1);
    public void ActualSize() => Scale = 1.0;

    private bool TryCurrentPage(out PageInfo page)
    {
        page = default!;
        var pages = Session?.Pages;
        if (pages is null || pages.Value.Length == 0) return false;
        page = pages.Value[Math.Clamp(CurrentPageIndex, 0, pages.Value.Length - 1)];
        return true;
    }

    public void FitWidth()
    {
        if (TryCurrentPage(out var p) && ViewportSize.Width > 0)
            Scale = ZoomMath.FitWidth(new Size(p.WidthPt, p.HeightPt), ViewportSize.Width);
    }

    public void FitPage()
    {
        if (TryCurrentPage(out var p) && ViewportSize.Width > 0 && ViewportSize.Height > 0)
            Scale = ZoomMath.FitPage(new Size(p.WidthPt, p.HeightPt), ViewportSize);
    }

    private void GoTo(int i) => CurrentPageIndex = Math.Clamp(i, 0, Math.Max(0, PageCount - 1));
    public void FirstPage() => GoTo(0);
    public void LastPage() => GoTo(PageCount - 1);
    public void NextPage() => GoTo(CurrentPageIndex + 1);
    public void PrevPage() => GoTo(CurrentPageIndex - 1);

    public string Title => Session is { IsDirty: true } ? _baseTitle + " •" : _baseTitle;

    /// <summary>다른 이름으로 저장 후 등 세션의 이름·변경 여부가 바뀌었을 때 부른다.</summary>
    public void RefreshTitle()
    {
        if (Session is not null) _baseTitle = Session.DisplayName;
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>이벤트 구독을 풀고, 세션 해제는 호출자가 한다.</summary>
    public void Detach()
    {
        Detached?.Invoke();
        if (Session is null) return;
        Session.Commands.Changed -= OnSessionChanged;
        Session.PagesChanged -= OnPagesChanged;
        Session.Annotations.Changed -= OnAnnotationsChanged;
    }

    private void OnSessionChanged() => _post(RefreshTitle);
    private void OnPagesChanged(PagesChangedEventArgs e) => _post(ApplyPages);

    private void ApplyPages()
    {
        var pages = Session!.Pages;
        PageCount = pages.Length;
        if (pages.Length == 0) return;
        int i = Math.Clamp(CurrentPageIndex, 0, pages.Length - 1);
        PageWidthMm = pages[i].WidthPt * PtToMm;
        PageHeightMm = pages[i].HeightPt * PtToMm;
        if (i != CurrentPageIndex) CurrentPageIndex = i;
    }

    // 현재 페이지가 바뀌면 상태 표시줄의 페이지 크기도 그 페이지 것으로 바꾼다.
    partial void OnCurrentPageIndexChanged(int value)
    {
        var pages = Session?.Pages;
        if (pages is null || pages.Value.Length == 0) return;
        var p = pages.Value[Math.Clamp(value, 0, pages.Value.Length - 1)];
        PageWidthMm = p.WidthPt * PtToMm;
        PageHeightMm = p.HeightPt * PtToMm;
    }
}
