using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using PdfEditor.TextEdit.Paragraphs;

namespace PdfEditor.App.Viewer.Objects;

/// <summary>화면의 문단 상자 점선 색을 정하는 상태: 보통(파랑) / 다른 글·그림과 겹침(주황, 확정 가능) / 쪽 아래를 넘어 확정 불가(빨강).</summary>
public enum ParagraphBoxState { Normal, Overlap, Blocked }

/// <summary>열려 있는 문단 편집 창의 상자 정보를 속성 패널의 "상자 폭" 칸과 주고받는 다리(UI 스레드 전용).</summary>
public sealed class ParagraphBoxBridge
{
    public bool Active { get; private set; }
    public double WidthPt { get; private set; }
    private Func<double, Task>? _request;

    public event Action? Changed;

    public void Set(bool active, double widthPt, Func<double, Task>? requestWidth)
    {
        Active = active;
        WidthPt = widthPt;
        _request = active ? requestWidth : null;
        Changed?.Invoke();
    }

    /// <summary>패널에서 폭(pt)을 바꾸라고 요청한다. 편집 창이 없으면 아무 일도 없다.</summary>
    public Task RequestWidthAsync(double widthPt) => _request?.Invoke(widthPt) ?? Task.CompletedTask;
}

/// <summary>문단 편집을 계획만 해 본 결과(화면 표시용). 문서는 바뀌지 않는다.</summary>
public sealed record ParagraphPreview(int LineCount, int MaxLines, bool Overflow, bool Overlap, bool UsedAppFont, bool FontsMixed,
    ParagraphRefusal Refusal, IReadOnlyList<int> Missing, bool NoChange, PdfRectF? DisplayBox, IReadOnlyList<string>? PlannedLines = null)
{
    public static ParagraphPreview Empty { get; } = new(0, int.MaxValue, false, false, false, false, ParagraphRefusal.None, [], true, null);

    /// <summary>확정할 수 있는가. 쪽 아래를 넘거나 쓸 수 없는 글자가 있으면 못 한다. 겹침은 막지 않는다(C6).</summary>
    public bool CanCommit => Refusal == ParagraphRefusal.None;

    public ParagraphBoxState State => !CanCommit ? ParagraphBoxState.Blocked : Overlap ? ParagraphBoxState.Overlap : ParagraphBoxState.Normal;

    public string StatusText
    {
        get
        {
            string max = MaxLines == int.MaxValue ? "∞" : MaxLines.ToString();
            var parts = new List<string>();
            switch (Refusal)
            {
                case ParagraphRefusal.Overflow: return string.Format(Strings.Para_Overflow, LineCount, max);
                case ParagraphRefusal.MissingGlyphs:
                    return string.Format(Strings.Para_Missing, string.Join(" ", Missing.Select(char.ConvertFromUtf32)));
                case ParagraphRefusal.Encrypted: return Strings.Para_Encrypted;
                case ParagraphRefusal.NotParagraph: return Strings.Para_ReasonSingle;
                case ParagraphRefusal.None: break;
                default: return Strings.Msg_ParagraphCommitFailed;
            }
            parts.Add(string.Format(Strings.Para_Lines, LineCount, max));
            if (UsedAppFont) parts.Add(Strings.Para_AppFont);
            if (FontsMixed) parts.Add(Strings.Para_FontsMixed);
            if (Overlap) parts.Add(Strings.Para_Overlap);
            return string.Join('\n', parts);   // 안내는 문장마다 한 줄(2026-10-27: 한 줄에 이어 붙이면 좁은 창에서 접혀 읽기 어려웠다)
        }
    }
}

/// <summary>문단 편집을 시작하려 한 결과. Session이 있으면 문단 편집 창, 없으면 한 줄 편집(M4)으로 되돌아가며 Reason(있으면)을 알린다.</summary>
public sealed record ParagraphStart(ParagraphSession? Session, string? Reason, bool InForm = false);

public sealed record ParagraphCommitResult(bool Committed, ParagraphPreview Preview, string? Toast);

/// <summary>
/// 문단 편집 한 번의 상태(UI 스레드 전용). 추정된 문단 모델, 줄 목록(위·아래 줄 포함/제외용), 이전 편집(다시 편집), 처음 글.
/// 폭 조절은 모델의 <see cref="ParagraphModel.ColumnWidthOverride"/>와 상자로 표현한다.
/// </summary>
public sealed class ParagraphSession
{
    public required PageId Page { get; init; }
    public required ObjectRef Seed { get; init; }
    public required ParagraphModel Model { get; init; }
    public required IReadOnlyList<TextLine> AllLines { get; init; }
    public required IReadOnlyList<Obstacle> Obstacles { get; init; }
    public ReplaceParagraphEdit? Previous { get; init; }
    public required string InitialText { get; init; }
    public KoreanLineBreak Korean { get; init; } = KoreanLineBreak.Word;
    internal CachedFontSource FontSource { get; init; } = new(new SessionFileFontSource());

    /// <summary>위·아래 줄 포함/제외는 처음 글 그대로일 때, 처음 편집일 때만 쓴다(이전 편집을 다시 여는 경우는 줄 목록이 없다).</summary>
    public bool CanExtend => Previous is null;

    /// <summary>글 흐름 방향(0·90·180·270).</summary>
    public int Angle => Model.Lines[0].Angle;

    public float SizePt => Model.Style.SizePt;

    /// <summary>지금 열 폭(pt): 끌어 정한 폭이 있으면 그것, 없으면 상자 폭.</summary>
    public double WidthPt => Model.ColumnWidthOverride ?? BoxU().U1 - BoxU().U0;

    public (double U0, double U1) BoxU()
    {
        var (u0, u1) = ParagraphGeometry.ProjectU(Model.Box, Angle);
        return (u0, u1);
    }

    /// <summary>위·아래 줄을 더하거나(양수) 뺀 새 세션. 처음 글(원문)은 새 줄 묶음의 원문으로 바뀐다.</summary>
    public ParagraphSession Extended(int topDelta, int bottomDelta)
    {
        var m = ParagraphDetector.Extend(Model, topDelta, bottomDelta, AllLines, Obstacles);
        // 사용자가 직접 고른 줄이므로 문단으로 본다(Extend는 이유를 비운다). 한 줄뿐이면 문단이 아니다.
        return new ParagraphSession
        {
            Page = Page, Seed = Seed, Model = m, AllLines = AllLines, Obstacles = Obstacles, Previous = null,
            InitialText = ParagraphLayout.OriginalText(m, new LayoutOptions { Korean = Korean }), Korean = Korean,
        };
    }

    /// <summary>
    /// 상자 가장자리를 끌어 폭을 바꾼 새 세션. leadingEdge면 글 시작 쪽 가장자리(왼쪽)를 끈 것: 폭이 바뀌고 시작 위치도 같이 움직인다.
    /// 반환 세션의 글 시작은 바뀌지 않는다(끝쪽 가장자리일 때).
    /// </summary>
    public ParagraphSession WithWidth(double newWidthPt, bool moveStart)
    {
        double min = 2 * SizePt;
        newWidthPt = Math.Max(min, newWidthPt);
        var (u0, u1) = BoxU();
        double oldW = WidthPt;
        double nu0 = moveStart ? u0 + (oldW - newWidthPt) : u0;
        var box = ParagraphGeometry.BoxWithU(Model.Box, Angle, nu0, nu0 + newWidthPt);
        return new ParagraphSession
        {
            Page = Page, Seed = Seed, Model = Model with { Box = box, ColumnWidthOverride = newWidthPt }, AllLines = AllLines, Obstacles = Obstacles,
            Previous = Previous, InitialText = InitialText, Korean = Korean, FontSource = FontSource, WidthChanged = true,
        };
    }

    /// <summary>이 편집에서 사용자가 폭을 바꿨는가(글이 그대로여도 줄바꿈을 다시 맞춰야 한다).</summary>
    public bool WidthChanged { get; init; }
}

/// <summary>글 흐름 방향별 좌표 계산(TextEdit의 내부 Geo와 같은 규칙, 화면 배치용).</summary>
public static class ParagraphGeometry
{
    public static (double Ux, double Uy, double Vx, double Vy) Axes(int angle) => angle switch
    {
        0 => (1, 0, 0, 1),
        90 => (0, 1, -1, 0),
        180 => (-1, 0, 0, -1),
        _ => (0, -1, 1, 0),
    };

    public static (double U0, double U1) ProjectU(PdfRectF r, int angle)
    {
        var (ux, uy, _, _) = Axes(angle);
        double a = r.Left * ux + r.Bottom * uy, b = r.Right * ux + r.Top * uy;
        double c = r.Left * ux + r.Top * uy, d = r.Right * ux + r.Bottom * uy;
        return (Math.Min(Math.Min(a, b), Math.Min(c, d)), Math.Max(Math.Max(a, b), Math.Max(c, d)));
    }

    /// <summary>글 방향 좌표 u의 범위만 바꾼 상자(세로 범위는 그대로).</summary>
    public static PdfRectF BoxWithU(PdfRectF box, int angle, double u0, double u1) => angle switch
    {
        0 => new PdfRectF((float)u0, box.Bottom, (float)u1, box.Top),
        90 => new PdfRectF(box.Left, (float)u0, box.Right, (float)u1),
        180 => new PdfRectF((float)-u1, box.Bottom, (float)-u0, box.Top),
        _ => new PdfRectF(box.Left, (float)-u1, box.Right, (float)-u0),
    };

    /// <summary>다시 맞춘 줄 하나가 차지하는 쪽 좌표 사각형(기준선 아래 0.25 em ~ 위 0.8 em).</summary>
    public static PdfRectF LineRect(PdfPointF origin, double width, double size, int angle)
    {
        var (ux, uy, vx, vy) = Axes(angle);
        PdfPointF P(double u, double v) => new((float)(origin.X + u * ux + v * vx), (float)(origin.Y + u * uy + v * vy));
        return PdfRectF.FromPoints([P(0, -0.25 * size), P(width, -0.25 * size), P(0, 0.8 * size), P(width, 0.8 * size)]);
    }

    public static PdfRectF Union(PdfRectF a, PdfRectF b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Bottom, b.Bottom), Math.Max(a.Right, b.Right), Math.Max(a.Top, b.Top));
}

/// <summary>쪽 버전마다 한 번만 원본 파일을 읽도록 코드북 만들기 결과를 기억한다(편집 창이 열려 있는 동안 같은 줄에 대해 반복해 부른다).</summary>
internal sealed class CachedFontSource(IParagraphFontSource inner) : IParagraphFontSource
{
    private ImmutableArray<TextLine> _lines;
    private bool _has;
    private PdfEditor.ContentStream.Fonts.FontCodebook? _book;
    private double _tc, _tz = 100;
    private string? _reason;

    public PdfEditor.ContentStream.Fonts.FontCodebook? Build(DocumentSession s, PageId page, ParagraphModel p, out double tc, out double tz, out string? reason)
    {
        if (!_has || _lines != p.Lines)
        {
            _book = inner.Build(s, page, p, out _tc, out _tz, out _reason);
            _lines = p.Lines;
            _has = true;
        }
        tc = _tc; tz = _tz; reason = _reason;
        return _book;
    }
}

/// <summary>
/// 문단 편집 화면의 계산(Avalonia 없음, M5 Task 8): 클릭 → 문단 추정(없으면 한 줄 편집 이유), 입력 중 미리 계획, 확정.
/// 읽기·계획은 PDFium 전용 스레드에서, 실제 변경은 <see cref="ParagraphEditCommand"/> 하나(실행 취소 한 번).
/// </summary>
public static class ParagraphController
{
    /// <summary>한 줄 편집으로 되돌아가는 이유. 이웃한 줄이 없는 외톨이 줄은 평범한 한 줄 편집이므로 알리지 않는다(null).</summary>
    public static string? ReasonText(ParagraphModel? m) => m switch
    {
        null => Strings.Para_ReasonFailed,
        { LowConfidenceReason: ParagraphReasons.TableRow } => Strings.Para_ReasonTable,
        { LowConfidenceReason: ParagraphReasons.MixedStyle } => Strings.Para_ReasonMixed,
        { LowConfidenceReason: ParagraphReasons.LowConfidence } => Strings.Para_ReasonLow,
        _ => null,
    };

    /// <summary>이 줄이 이미 고친 문단(<see cref="ReplaceParagraphEdit"/>)의 새 줄이거나, 그 문단에 남은 원본 줄이면 그 편집.</summary>
    public static ReplaceParagraphEdit? FindPrevious(IReadOnlyList<ContentEdit> edits, ObjectRef seed)
    {
        foreach (var e in edits.OfType<ReplaceParagraphEdit>())
        {
            if (seed.IsInserted)
            {
                if (e.OriginalFontLines.Any(l => l.LineId == seed.InsertedId) || e.AppLineIds.Contains(seed.InsertedId)) return e;
            }
            else if (e.Meta.Model is ParagraphModel m && m.AllRefs.Contains(seed)) return e;
        }
        return null;
    }

    /// <summary>문단 편집을 시작한다. 문단으로 볼 수 없으면 Session이 null(한 줄 편집으로)이고 이유가 문구로 온다. 폼 안의 글이면 InForm.</summary>
    public static async Task<ParagraphStart> StartAsync(DocumentTabViewModel tab, PageId page, PageObjectEntry pick, PageObjectIndex idx)
    {
        if (tab.OneLineOnly) return new ParagraphStart(null, null);
        if (tab.Session is not { } s) return new ParagraphStart(null, null);
        if (!tab.CanUseAppFontText) return new ParagraphStart(null, Strings.Para_Encrypted);
        try
        {
            return await s.Thread.InvokeAsync(() =>
            {
                if (s.IndexOf(page) < 0) return new ParagraphStart(null, Strings.Para_ReasonFailed);
                var prev = FindPrevious(s.Content.For(page), pick.Ref);
                if (prev is { Meta.Model: ParagraphModel pm })
                {
                    return new ParagraphStart(new ParagraphSession
                    {
                        Page = page, Seed = pick.Ref, Model = pm, AllLines = [], Obstacles = [], Previous = prev, InitialText = prev.Meta.NewText,
                    }, null);
                }
                if (pick.Ref.IsInserted) return new ParagraphStart(null, null);
                var det = ParagraphReader.Detect(s, page, pick.Ref, idx);
                if (det.Input.Refusal == ParagraphReadRefusal.InForm) return new ParagraphStart(null, null, InForm: true);
                if (det.Input.Refusal != ParagraphReadRefusal.None) return new ParagraphStart(null, null);
                if (det.Model is not { IsParagraph: true } m) return new ParagraphStart(null, ReasonText(det.Model));
                var korean = ParagraphEditPlanner.DecideKorean(det.Lines, det.Input.Obstacles);
                return new ParagraphStart(new ParagraphSession
                {
                    Page = page, Seed = pick.Ref, Model = m, AllLines = det.Lines, Obstacles = det.Input.Obstacles,
                    InitialText = ParagraphLayout.OriginalText(m, new LayoutOptions { Korean = korean }), Korean = korean,
                }, null);
            });
        }
        catch (Exception) { return new ParagraphStart(null, Strings.Para_ReasonFailed); }
    }

    private static ParagraphPlan PlanOn(DocumentSession s, ParagraphSession ps, string text)
    {
        if (s.IndexOf(ps.Page) < 0) return ParagraphPlan.Refused(ParagraphRefusal.NotParagraph);
        var font = DocumentTabViewModel.Appearance?.Font;
        return ParagraphEditPlanner.Plan(s, ps.Page, ps.Model, text, font, ps.Previous, ps.Korean, ps.FontSource, DocumentTabViewModel.Appearance?.Catalog);
    }

    private static ParagraphPreview ToPreview(ParagraphPlan plan, ParagraphSession ps)
    {
        var l = plan.Layout;
        PdfRectF? box = ps.Model.Box;
        if (l is not null)
            foreach (var line in l.Lines)
                box = ParagraphGeometry.Union(box.Value, ParagraphGeometry.LineRect(line.BaselineOrigin, line.Width, ps.SizePt, ps.Angle));
        return new ParagraphPreview(l?.Lines.Length ?? 0, l?.MaxLines ?? int.MaxValue, plan.Refusal == ParagraphRefusal.Overflow, plan.Overlap,
            plan.UsedAppFont, plan.FontsMixed, plan.Refusal, plan.Missing, plan.NoChange, box, l?.Lines.Select(x => x.Text).ToArray());
    }

    /// <summary>입력 중 미리 계획(문서는 바뀌지 않음): 줄 수·넘침·겹침·글꼴 알림.</summary>
    public static async Task<ParagraphPreview> PreviewAsync(DocumentTabViewModel tab, ParagraphSession ps, string text)
    {
        if (tab.Session is not { } s) return ParagraphPreview.Empty;
        try
        {
            var plan = await s.Thread.InvokeAsync(() => PlanOn(s, ps, text));
            return ToPreview(plan, ps);
        }
        catch (Exception) { return ParagraphPreview.Empty with { Refusal = ParagraphRefusal.NotParagraph }; }
    }

    /// <summary>글을 확정한다. 계획이 거절되면(쪽 아래 넘침 등) Committed=false로 문서를 바꾸지 않는다. 바뀐 것이 없으면 Committed=true이지만 기록은 만들지 않는다.</summary>
    public static async Task<ParagraphCommitResult> CommitAsync(DocumentTabViewModel tab, ParagraphSession ps, string text)
    {
        if (tab.Session is not { } s || !tab.CanEditContent) return new ParagraphCommitResult(true, ParagraphPreview.Empty, null);
        if (text == ps.InitialText && !ps.WidthChanged) return new ParagraphCommitResult(true, ParagraphPreview.Empty, null);   // 열었다 그냥 닫기: 기록 0
        try
        {
            var plan = await s.Thread.InvokeAsync(() => PlanOn(s, ps, text));
            var preview = ToPreview(plan, ps);
            if (!plan.Ok) return new ParagraphCommitResult(false, preview, null);
            if (plan.ToCommand(ps.Page) is { } cmd) await tab.RunParagraphCommandAsync(cmd);
            string? toast = null;
            if (plan.UsedAppFont) toast = plan.FontsMixed ? Strings.Msg_TextFallbackApp + " " + Strings.Para_FontsMixed : Strings.Msg_TextFallbackApp;
            return new ParagraphCommitResult(true, preview, toast);
        }
        catch (Exception) { return new ParagraphCommitResult(false, ParagraphPreview.Empty with { Refusal = ParagraphRefusal.NotParagraph }, null); }
    }
}
