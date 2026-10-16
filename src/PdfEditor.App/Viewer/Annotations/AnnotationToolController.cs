using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Viewer.Annotations;

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

    public void Begin(ToolKind tool, ToolStyle style, PageId page, PdfPointF p, float ptPerDip)
    {
        _tool = tool; _style = style; _page = page; _start = p; _ptPerDip = ptPerDip;
        _points.Clear();
        _points.Add(p);
        _active = true;
        _last = p;
        if (tool == ToolKind.Eraser) { BeginErase(p); return; }
        Preview = Build(p);
    }

    public void Move(PdfPointF p)
    {
        if (!_active) return;
        if (_tool == ToolKind.Eraser) { EraseTo(p); return; }
        if (_tool == ToolKind.Pencil) _points.Add(p);
        Preview = Build(p);
    }

    public ChangeAnnotationsCommand? End(PdfPointF p)
    {
        if (!_active) return null;
        if (_tool == ToolKind.Eraser) return EndErase(p);
        if (_tool == ToolKind.Pencil && _points[^1] != p) _points.Add(p);
        var a = Build(p);
        _active = false;
        Preview = null;
        _points.Clear();
        if (a is null) return null;
        if (_tool == ToolKind.StickyNote) LastCreatedSticky = a;
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
            default:
                return null;
        }
    }

    private Annotation Finish(ToolKind tool, ToolStyle style, Annotation a)
    {
        var now = clock();
        a = a with
        {
            Id = newId(),
            Color = style.Color,
            Opacity = style.Opacity,
            Author = author(),
            Subject = subject(tool),
            CreatedAt = now,
            ModifiedAt = now,
            Flags = PdfAnnotFlags.Print,
        };
        return a.Kind is AnnotKind.Ink or AnnotKind.Highlight or AnnotKind.Underline or AnnotKind.StrikeOut
            ? a with { Rect = AnnotationGeometry.ComputeRect(a) }
            : a;
    }
}
