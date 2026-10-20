using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Viewer.Annotations;

/// <summary>글자 사이 경계: X = 경계 위치, Bottom = 글자 바닥, Height = 그 글자 높이 (모두 PDF pt).</summary>
public readonly record struct CharBoundary(float X, float Bottom, float Height);

/// <summary>
/// 끌기 한 번으로 주석을 만드는 도구 로직. Avalonia를 참조하지 않는다(좌표는 PDF pt).
/// UI 스레드에서만 쓴다. 명령 실행(문서 반영)은 호출한 쪽이 한다.
/// </summary>
public sealed class AnnotationToolController(
    Func<AnnotId> newId, Func<DateTimeOffset> clock, Func<string> author, Func<ToolKind, string> subject)
{
    private ToolKind _tool;
    private ToolStyle? _style;
    private PageId _page;
    private PdfPointF _start;
    private float _ptPerDip = 1f;
    private readonly List<PdfPointF> _points = [];
    private bool _active;

    private PdfPointF _last;
    private readonly Dictionary<AnnotId, Annotation> _eraseOriginal = [];
    private readonly Dictionary<AnnotId, Annotation?> _eraseState = [];
    private const float EraserRadiusDip = 6f;

    /// <summary>지우개가 대상으로 삼을 쪽의 주석 목록. 호스트가 연결한다.</summary>
    public Func<PageId, IReadOnlyList<Annotation>>? PageAnnotationSource { get; set; }

    /// <summary>지우개 진행 중 바뀐 주석의 임시 모습 (다 지운 것은 null). 바뀐 것이 없으면 null.</summary>
    public IReadOnlyDictionary<AnnotId, Annotation?>? Previews { get; private set; }

    public Annotation? Preview { get; private set; }
    public Annotation? LastCreatedSticky { get; private set; }

    // --- M3a ---

    /// <summary>패치가 필요한 도구를 시작해도 되는가 (암호 PDF 등은 false). 호스트가 연결한다.</summary>
    public Func<bool> CanUsePatchFeatures { get; set; } = () => false;

    /// <summary>타자기 상자를 글에 맞추는 함수 (호스트가 AnnotationPainter.FitTypewriter에 연결). 만들 때와 확정할 때 쓴다.</summary>
    public Func<Annotation, Annotation>? FitTypewriter { get; set; }

    /// <summary>스탬프 표시 문구 (리소스에서 가져온다 — 이 파일에는 한글 리터럴을 두지 않는다).</summary>
    public Func<StampSpec, string> StampLabel { get; set; } = s => s.Text ?? s.BuiltinName;

    /// <summary>마지막으로 고른 스탬프 (기본: 승인).</summary>
    public StampSpec LastStamp { get; set; } = new(StampKind.Builtin, "Approved", null, null);

    /// <summary>그림·첨부 바이트 보관소. AppearanceServices.Assets(세션 공유 인스턴스)를 연결한다. 새로 만들지 않는다.</summary>
    public Func<AnnotationAssets>? Assets { get; set; }

    /// <summary>주석 이름(/NM). 만들 때 한 번만 붙인다 — 저장할 때마다 새로 만들지 않는다.</summary>
    public Func<string> NewName { get; set; } = () => Guid.NewGuid().ToString("N");

    /// <summary>글자 경계 찾기 (쪽, 쪽 좌표, 허용 오차 pt). M1 텍스트 API(GetCharIndexAtPos)를 호스트가 감싸서 연결한다.</summary>
    public Func<PageId, PdfPointF, double, CharBoundary?>? CharBoundaryAt { get; set; }

    /// <summary>방금 만든 주석 중 편집 창을 열어야 하는 것 (텍스트 상자·타자기·설명선·삽입 Caret·바꾸기 Caret). 없으면 null.</summary>
    public Annotation? LastCreatedEdit { get; private set; }

    /// <summary>다중 클릭 도구(폴리라인·다각형)가 점을 모으는 중인가.</summary>
    public bool IsCollectingPoints => _active && IsPolyTool(_tool);
    public int CollectedPointCount => IsCollectingPoints ? _points.Count : 0;

    /// <summary>파일 첨부: 클릭한 자리. 파일을 고르면 <see cref="AttachFile"/>이 만든다.</summary>
    public (PageId Page, PdfPointF At)? PendingAttach { get; private set; }

    /// <summary>마지막 <see cref="AttachFile"/>이 파일 크기 등으로 거절됐다.</summary>
    public bool LastAttachRejected { get; private set; }

    public const float DefaultTextBoxWidthPt = 200f, DefaultTextBoxHeightPt = 40f;
    public const float DefaultCalloutWidthPt = 160f, DefaultCalloutHeightPt = 40f;
    public const float StampWidthPt = 150f, StampHeightPt = 50f;
    public const float AttachIconPt = 20f;
    private const float TinyDragDip = 4f;
    private const float SnapStepDeg = 15f;

    public static bool IsPatchTool(ToolKind t) => t is ToolKind.Line or ToolKind.Arrow or ToolKind.PolyLine or ToolKind.Polygon
        or ToolKind.Cloud or ToolKind.TextBox or ToolKind.Typewriter or ToolKind.Callout or ToolKind.InsertText
        or ToolKind.ReplaceText or ToolKind.Stamp or ToolKind.FileAttach;

    private static bool IsPolyTool(ToolKind t) => t is ToolKind.PolyLine or ToolKind.Polygon;

    private bool _shift;
    private PdfPointF _hover;

    public void Begin(ToolKind tool, ToolStyle style, PageId page, PdfPointF p, float ptPerDip)
    {
        if (IsPatchTool(tool) && !CanUsePatchFeatures())
        {
            Cancel();
            return;
        }
        if (_active && IsPolyTool(tool) && tool == _tool && page == _page)
        {
            AddPolyPoint(p);
            return;
        }
        if (tool != ToolKind.FileAttach) PendingAttach = null;
        Cancel();
        _tool = tool; _style = style; _page = page; _start = p; _ptPerDip = ptPerDip;
        _points.Clear();
        _points.Add(p);
        _active = true;
        _last = p;
        _hover = p;
        _shift = false;
        LastCreatedEdit = null;
        if (tool == ToolKind.Eraser) { BeginErase(p); return; }
        if (IsPolyTool(tool)) { Preview = BuildPoly(p); return; }
        Preview = BuildPreview(p);
    }

    public void Move(PdfPointF p, bool shift = false)
    {
        if (!_active) return;
        _shift = shift;
        _hover = p;
        if (_tool == ToolKind.Eraser) { EraseTo(p); return; }
        if (_tool == ToolKind.Pencil) _points.Add(p);
        Preview = IsPolyTool(_tool) ? BuildPoly(p) : BuildPreview(p);
    }

    public ChangeAnnotationsCommand? End(PdfPointF p, bool shift = false)
    {
        if (!_active) return null;
        if (IsPolyTool(_tool)) { _hover = p; Preview = BuildPoly(p); return null; }   // 점은 계속 모은다 (끝내기는 FinishPolyline)
        if (_tool == ToolKind.Eraser) return EndErase(p);
        _shift = shift;
        if (_tool == ToolKind.Pencil && _points[^1] != p) _points.Add(p);
        if (_tool == ToolKind.FileAttach)
        {
            PendingAttach = (_page, p);
            Cancel();
            return null;
        }
        var a = Build(p);
        _active = false;
        Preview = null;
        _points.Clear();
        if (a is null) return null;
        if (_tool == ToolKind.StickyNote) LastCreatedSticky = a;
        if (_tool is ToolKind.TextBox or ToolKind.Typewriter or ToolKind.Callout or ToolKind.InsertText) LastCreatedEdit = a;
        return ChangeAnnotationsCommand.Add(_page, a);
    }

    public void Cancel()
    {
        _active = false;
        Preview = null;
        Previews = null;
        _eraseOriginal.Clear();
        _eraseState.Clear();
        _points.Clear();
    }

    // ---- 폴리라인·다각형 ----

    private void AddPolyPoint(PdfPointF p)
    {
        // 더블클릭의 둘째 클릭처럼 1 DIP 안에 겹친 점은 더하지 않는다.
        if (Dist(_points[^1], p) >= MinPointSpacingDip * _ptPerDip) _points.Add(p);
        _hover = p;
        Preview = BuildPoly(p);
    }

    /// <summary>더블클릭·Enter: 모은 점으로 주석을 만든다. 점이 모자라면(폴리라인 2, 다각형 3) 아무것도 만들지 않는다.</summary>
    public ChangeAnnotationsCommand? FinishPolyline()
    {
        if (!IsCollectingPoints) return null;
        var a = BuildPoly(null);
        var page = _page;
        Cancel();
        return a is null ? null : ChangeAnnotationsCommand.Add(page, a);
    }

    /// <summary>Backspace: 마지막 점을 취소한다. 점이 다 없어지면 그리기를 끝낸다.</summary>
    public void RemoveLastPoint()
    {
        if (!IsCollectingPoints) return;
        _points.RemoveAt(_points.Count - 1);
        if (_points.Count == 0) { Cancel(); return; }
        Preview = BuildPoly(_hover);
    }

    private Annotation? BuildPoly(PdfPointF? hover)
    {
        var pts = new List<PdfPointF>(_points);
        if (hover is { } h && Dist(pts[^1], h) >= MinPointSpacingDip * _ptPerDip) pts.Add(h);
        int min = _tool == ToolKind.Polygon ? 3 : 2;
        if (hover is null && pts.Count < min) return null;
        if (pts.Count < 2) return null;   // 미리보기도 선분이 하나는 있어야 한다
        var style = _style!;
        return Finish(_tool, style, new Annotation
        {
            Kind = _tool == ToolKind.Polygon ? AnnotKind.Polygon : AnnotKind.PolyLine,
            Vertices = [.. pts],
            BorderWidth = style.Width,
            FillColor = _tool == ToolKind.Polygon ? style.Fill : null,
        });
    }

    // ---- 글자에 붙는 도구 ----

    public ChangeAnnotationsCommand? CreateMarkup(ToolKind tool, ToolStyle style, PageId page, IReadOnlyList<PdfRectF> textRects)
    {
        if (textRects.Count == 0) return null;
        var kind = tool switch
        {
            ToolKind.HighlightText => AnnotKind.Highlight,
            ToolKind.Underline => AnnotKind.Underline,
            ToolKind.StrikeOut => AnnotKind.StrikeOut,
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };
        return ChangeAnnotationsCommand.Add(page, Finish(tool, style, new Annotation
        {
            Kind = kind,
            Quads = [.. textRects.Select(PdfQuadF.FromRect)],
        }));
    }

    /// <summary>
    /// 텍스트 삽입: 글자 경계에 Caret(높이 = 그 글자 높이). 편집 창을 열 주석은 <see cref="LastCreatedEdit"/>.
    /// 패치 도구라 <see cref="CanUsePatchFeatures"/>가 false면 만들지 않는다.
    /// </summary>
    public ChangeAnnotationsCommand? CreateInsertCaret(ToolStyle style, PageId page, CharBoundary at)
    {
        if (!CanUsePatchFeatures() || at.Height <= 0) return null;
        var caret = BuildCaret(ToolKind.InsertText, style, at);
        LastCreatedEdit = caret;
        return ChangeAnnotationsCommand.Add(page, caret);
    }

    /// <summary>
    /// 텍스트 바꾸기: 선택한 글자의 취소선(StrikeOut) + 끝 쪽 Caret을 묶음(Caret의 InReplyTo → StrikeOut, Group)으로 한 명령에 담는다.
    /// textRects는 M2 텍스트 마크업과 같은 글자 사각형(읽는 순서). 편집 창은 Caret(<see cref="LastCreatedEdit"/>)에 연다.
    /// </summary>
    public ChangeAnnotationsCommand? CreateReplace(ToolStyle style, PageId page, IReadOnlyList<PdfRectF> textRects)
    {
        if (!CanUsePatchFeatures() || textRects.Count == 0) return null;
        var strike = Finish(ToolKind.ReplaceText, style, new Annotation
        {
            Kind = AnnotKind.StrikeOut,
            Quads = [.. textRects.Select(PdfQuadF.FromRect)],
        });
        var last = textRects[^1];
        var caret = Finish(ToolKind.ReplaceText, style, new Annotation
        {
            Kind = AnnotKind.Caret,
            Rect = CaretRect(new CharBoundary(last.Right, last.Bottom, last.Height)),
            Contents = "",
            InReplyTo = strike.Id,
            ReplyKind = ReplyType.Group,
        });
        LastCreatedEdit = caret;
        return new ChangeAnnotationsCommand(page, [new AnnotationChange(null, strike), new AnnotationChange(null, caret)]);
    }

    private static PdfRectF CaretRect(CharBoundary at)
    {
        float w = Math.Max(6f, at.Height * 0.6f);
        return new PdfRectF(at.X - w / 2f, at.Bottom, at.X + w / 2f, at.Bottom + at.Height);
    }

    /// <summary>파일 첨부: 파일을 고른 뒤 부른다 (파일 열기 창은 호출한 쪽). 클릭한 자리가 없거나 크기 등으로 거절되면 null.</summary>
    public ChangeAnnotationsCommand? AttachFile(string name, byte[] data)
    {
        LastAttachRejected = false;
        if (PendingAttach is not { } pending || !CanUsePatchFeatures()) return null;
        var assets = RequireAssets();
        string id;
        try { id = assets.AddFile(name, data); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            LastAttachRejected = true;
            return null;
        }
        PendingAttach = null;
        var style = ToolDefaults.For(ToolKind.FileAttach);
        var a = Finish(ToolKind.FileAttach, style, new Annotation
        {
            Kind = AnnotKind.FileAttachment,
            Rect = new PdfRectF(pending.At.X, pending.At.Y - AttachIconPt, pending.At.X + AttachIconPt, pending.At.Y),
            AttachmentId = id,
            AttachmentName = name,
            Contents = name,
        });
        return ChangeAnnotationsCommand.Add(pending.Page, a);
    }

    public void CancelAttach() => PendingAttach = null;

    /// <summary>타자기를 확정하기 전에 부른다: 상자가 글에 맞는다. 다른 유형은 그대로.</summary>
    public Annotation FitForCommit(Annotation a)
        => a is { Kind: AnnotKind.FreeText, Intent: FreeTextIntent.Typewriter } ? RequireFit()(a) : a;

    /// <summary>설명선 지시선은 점이 2~3개여야 한다: 2개 미만이면 null, 3개 넘으면 처음·가운데·끝만 남긴다.</summary>
    public static ImmutableArray<PdfPointF>? NormalizeCalloutLeader(IReadOnlyList<PdfPointF> pts)
    {
        if (pts.Count < 2) return null;
        if (pts.Count <= 3) return [.. pts];
        return [pts[0], pts[pts.Count / 2], pts[^1]];
    }

    private void BeginErase(PdfPointF p)
    {
        _eraseOriginal.Clear();
        _eraseState.Clear();
        Previews = null;
        foreach (var a in PageAnnotationSource?.Invoke(_page) ?? [])
            if (a.Kind == AnnotKind.Ink && !a.InkStrokes.IsDefaultOrEmpty) _eraseOriginal[a.Id] = a;
        EraseAt(p);
    }

    private void EraseTo(PdfPointF p)
    {
        float spacing = Math.Max(EraserRadiusDip * _ptPerDip / 2f, 1e-3f);
        foreach (var q in InkEraser.SamplePath(_last, p, spacing)) EraseAt(q);
        _last = p;
    }

    private void EraseAt(PdfPointF center)
    {
        float r = EraserRadiusDip * _ptPerDip;
        foreach (var (id, orig) in _eraseOriginal)
        {
            if (_eraseState.TryGetValue(id, out var st) && st is null) continue;
            var cur = st ?? orig;
            var strokes = InkEraser.EraseCircle(cur.InkStrokes, center, r + cur.BorderWidth / 2f);
            if (strokes == cur.InkStrokes) continue;
            _eraseState[id] = strokes.IsEmpty ? null : cur with { InkStrokes = strokes };
        }
        Previews = _eraseState.Count == 0 ? null : new Dictionary<AnnotId, Annotation?>(_eraseState);
    }

    private ChangeAnnotationsCommand? EndErase(PdfPointF p)
    {
        EraseTo(p);
        var changes = new List<AnnotationChange>();
        foreach (var (id, st) in _eraseState)
        {
            var orig = _eraseOriginal[id];
            if (st is null) { changes.Add(new AnnotationChange(orig, null)); continue; }
            var after = st with { ModifiedAt = clock() };
            changes.Add(new AnnotationChange(orig, after with { Rect = AnnotationGeometry.ComputeRect(after) }));
        }
        var page = _page;
        Cancel();
        return changes.Count == 0 ? null : new ChangeAnnotationsCommand(page, changes);
    }

    private Annotation? Build(PdfPointF end)
    {
        var style = _style!;
        switch (_tool)
        {
            case ToolKind.Rectangle:
            case ToolKind.Ellipse:
            case ToolKind.HighlightArea:
                {
                    var r = new PdfRectF(Math.Min(_start.X, end.X), Math.Min(_start.Y, end.Y),
                                         Math.Max(_start.X, end.X), Math.Max(_start.Y, end.Y));
                    if (r.Width < AnnotationGeometry.MinShapeSizePt || r.Height < AnnotationGeometry.MinShapeSizePt)
                        return null;
                    if (_tool == ToolKind.HighlightArea)
                        return Finish(_tool, style, new Annotation { Kind = AnnotKind.Highlight, Quads = [PdfQuadF.FromRect(r)] });
                    return Finish(_tool, style, new Annotation
                    {
                        Kind = _tool == ToolKind.Rectangle ? AnnotKind.Square : AnnotKind.Circle,
                        Rect = r,
                        BorderWidth = style.Width,
                        FillColor = style.Fill,
                    });
                }
            case ToolKind.Pencil:
                {
                    var pts = InkSimplifier.Simplify(_points, 0.5f * _ptPerDip);
                    return Finish(_tool, style, new Annotation
                    {
                        Kind = AnnotKind.Ink,
                        BorderWidth = style.Width,
                        InkStrokes = [[.. pts]],
                    });
                }
            case ToolKind.StickyNote:
                {
                    const float s = AnnotationGeometry.TextIconSizePt;
                    return Finish(_tool, style, new Annotation
                    {
                        Kind = AnnotKind.Text,
                        Rect = new PdfRectF(end.X, end.Y - s, end.X + s, end.Y),
                        Contents = "",
                    });
                }
            case ToolKind.Line:
            case ToolKind.Arrow:
                {
                    var e = _shift ? SnapAngle(_start, end) : end;
                    if (Dist(_start, e) < MinPointSpacingDip * _ptPerDip) return null;
                    return Finish(_tool, style, new Annotation
                    {
                        Kind = AnnotKind.Line,
                        Vertices = [_start, e],
                        BorderWidth = style.Width,
                        EndEnding = _tool == ToolKind.Arrow ? LineEnding.OpenArrow : LineEnding.None,
                    });
                }
            case ToolKind.Cloud:
                {
                    var r = DragRect(end);
                    if (r.Width < AnnotationGeometry.MinShapeSizePt || r.Height < AnnotationGeometry.MinShapeSizePt) return null;
                    return Finish(_tool, style, new Annotation
                    {
                        Kind = AnnotKind.Square, Rect = r, BorderWidth = style.Width, FillColor = style.Fill, CloudIntensity = 1f,
                    });
                }
            case ToolKind.TextBox:
                {
                    var r = DragRect(end);
                    float tiny = TinyDragDip * _ptPerDip;
                    // 작은 끌기(클릭)는 클릭한 자리를 왼쪽 위로 하는 기본 크기
                    if (r.Width < tiny && r.Height < tiny)
                        r = UprightAt(_start, new PdfRectF(_start.X, _start.Y - DefaultTextBoxHeightPt, _start.X + DefaultTextBoxWidthPt, _start.Y));
                    return Finish(_tool, style, new Annotation
                    {
                        Kind = AnnotKind.FreeText, Intent = FreeTextIntent.TextBox, Rect = r, Contents = "", Rotation = PageRotationDeg,
                        BorderWidth = style.Width, FillColor = style.Fill, TextColor = style.Color,
                    });
                }
            case ToolKind.Typewriter:
                {
                    const float w = 20f;
                    float h = 12f * 1.2f + 4f;
                    var a = Finish(_tool, style, new Annotation
                    {
                        Kind = AnnotKind.FreeText, Intent = FreeTextIntent.Typewriter, Contents = "",
                        Rect = UprightAt(_start, new PdfRectF(_start.X, _start.Y - h, _start.X + w, _start.Y)), Rotation = PageRotationDeg,
                        BorderWidth = 0f, TextColor = style.Color,
                    });
                    return RequireFit()(a);
                }
            case ToolKind.Callout:
                {
                    // 누른 점 = 지시선 끝, 놓은 곳 = 상자 왼쪽 위
                    var tip = _start;
                    var topLeft = Dist(tip, end) < TinyDragDip * _ptPerDip
                        ? AnnotationRotation.About(tip, AnnotationRotation.Quarter(PageRotationDeg)).Apply(new PdfPointF(tip.X + 40f * _ptPerDip, tip.Y + 40f * _ptPerDip))
                        : end;
                    var box = UprightAt(topLeft, new PdfRectF(topLeft.X, topLeft.Y - DefaultCalloutHeightPt, topLeft.X + DefaultCalloutWidthPt, topLeft.Y));
                    var leader = NormalizeCalloutLeader([tip, LeaderAttachPoint(box, tip)]);
                    if (leader is null) return null;
                    var a = Finish(_tool, style, new Annotation
                    {
                        Kind = AnnotKind.FreeText, Intent = FreeTextIntent.Callout, Contents = "", Rotation = PageRotationDeg,
                        TextBox = box, Vertices = leader.Value, EndEnding = LineEnding.OpenArrow,
                        BorderWidth = style.Width, FillColor = style.Fill, TextColor = style.Color,
                    });
                    return a with { Rect = AnnotationGeometry.ComputeRect(a) };
                }
            case ToolKind.InsertText:
                {
                    if (CharBoundaryAt is null || CharBoundaryAt(_page, end, TinyDragDip * _ptPerDip) is not { Height: > 0 } at) return null;
                    return BuildCaret(_tool, style, at);
                }
            case ToolKind.Stamp:
                {
                    var spec = LastStamp;
                    float w = StampWidthPt, h = StampHeightPt;
                    if (spec.Kind == StampKind.Image)
                    {
                        if (spec.ImageId is null || !RequireAssets().HasImage(spec.ImageId)) return null;
                        var (_, iw, ih, _) = RequireAssets().GetImage(spec.ImageId);
                        float k = StampWidthPt / Math.Max(iw, ih);   // 긴 변을 150pt에 맞춘다
                        w = iw * k; h = ih * k;
                    }
                    if (AnnotationRotation.Quarter(PageRotationDeg) % 2 == 1) (w, h) = (h, w);   // 돌아간 쪽: 화면에서 가로로 보이게
                    return Finish(_tool, style, new Annotation
                    {
                        Kind = AnnotKind.Stamp, Stamp = spec, Contents = StampLabel(spec), Rotation = PageRotationDeg,
                        Rect = ClampToPage(new PdfRectF(end.X - w / 2f, end.Y - h / 2f, end.X + w / 2f, end.Y + h / 2f)),
                    });
                }
            default:
                return null;
        }
    }

    private const float MinPointSpacingDip = 1f;

    private Annotation BuildCaret(ToolKind tool, ToolStyle style, CharBoundary at)
        => Finish(tool, style, new Annotation { Kind = AnnotKind.Caret, Rect = CaretRect(at), Contents = "" });

    /// <summary>쪽 회전(0~3, PDFium). 호스트가 연결한다. 글자가 있는 주석(FreeText·Stamp)이 화면에서 바로 서도록 Rotation을 정한다.</summary>
    public Func<PageId, int>? PageRotationOf { get; set; }

    private int PageRotationDeg => AnnotationRotation.ForPage(PageRotationOf?.Invoke(_page) ?? 0);

    /// <summary>바로 선(화면 기준) 사각형 r을 고정점 p를 중심으로 쪽 회전만큼 돌린다: 화면에서 본 왼쪽 위가 p에 온다.</summary>
    private PdfRectF UprightAt(PdfPointF p, PdfRectF r) => AnnotationRotation.Apply(AnnotationRotation.About(p, AnnotationRotation.Quarter(PageRotationDeg)), r);

    /// <summary>쪽 경계(호스트가 연결, 없으면 그대로). 스탬프가 쪽 밖으로 나가지 않게 안으로 민다.</summary>
    public Func<PageId, PdfRectF?>? PageBoundsOf { get; set; }

    private PdfRectF ClampToPage(PdfRectF r)
    {
        if (PageBoundsOf?.Invoke(_page) is not { } b) return r;
        float dx = r.Width >= b.Width ? 0f : Math.Max(b.Left - r.Left, Math.Min(0f, b.Right - r.Right));
        float dy = r.Height >= b.Height ? 0f : Math.Max(b.Bottom - r.Bottom, Math.Min(0f, b.Top - r.Top));
        return r.Offset(dx, dy);
    }

    private AnnotationAssets RequireAssets()
        => Assets?.Invoke() ?? throw new InvalidOperationException("AnnotationToolController.Assets is not wired (set it to the shared AppearanceServices.Assets).");

    private Func<Annotation, Annotation> RequireFit()
        => FitTypewriter ?? throw new InvalidOperationException("AnnotationToolController.FitTypewriter is not wired (set it to AnnotationPainter.FitTypewriter).");

    // 클릭으로 끝나는 도구는 미리보기가 없다 (글자 조회·파일 창을 움직임마다 부르지 않게)
    private Annotation? BuildPreview(PdfPointF p)
        => _tool is ToolKind.InsertText or ToolKind.FileAttach ? null : Build(p);

    private PdfRectF DragRect(PdfPointF end)
        => new(Math.Min(_start.X, end.X), Math.Min(_start.Y, end.Y), Math.Max(_start.X, end.X), Math.Max(_start.Y, end.Y));

    private static float Dist(PdfPointF a, PdfPointF b)
        => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Shift: 시작점에서 본 각도를 15° 단위로 맞춘다 (길이는 그대로).</summary>
    public static PdfPointF SnapAngle(PdfPointF from, PdfPointF to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len == 0) return to;
        float step = SnapStepDeg * MathF.PI / 180f;
        float ang = MathF.Round(MathF.Atan2(dy, dx) / step) * step;
        return new PdfPointF(from.X + len * MathF.Cos(ang), from.Y + len * MathF.Sin(ang));
    }

    /// <summary>지시선이 상자에 닿는 점: 끝점이 있는 쪽 변의 가운데.</summary>
    public static PdfPointF LeaderAttachPoint(PdfRectF box, PdfPointF tip)
    {
        float cx = (box.Left + box.Right) / 2f, cy = (box.Bottom + box.Top) / 2f;
        if (tip.X < box.Left) return new(box.Left, cy);
        if (tip.X > box.Right) return new(box.Right, cy);
        return tip.Y > box.Top ? new(cx, box.Top) : new(cx, box.Bottom);
    }

    private Annotation Finish(ToolKind tool, ToolStyle style, Annotation a)
    {
        var now = clock();
        a = a with
        {
            Id = newId(),
            Name = NewName(),
            Color = style.Color,
            Opacity = style.Opacity,
            Author = author(),
            Subject = subject(tool),
            CreatedAt = now,
            ModifiedAt = now,
            Flags = PdfAnnotFlags.Print,
        };
        return a.Kind is AnnotKind.Ink or AnnotKind.Highlight or AnnotKind.Underline or AnnotKind.StrikeOut
            or AnnotKind.Line or AnnotKind.PolyLine or AnnotKind.Polygon
            ? a with { Rect = AnnotationGeometry.ComputeRect(a) }
            : a;
    }
}
