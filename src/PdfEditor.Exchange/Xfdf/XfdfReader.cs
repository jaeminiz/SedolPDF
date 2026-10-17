using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Exchange.Xfdf;

/// <summary>XFDF를 읽을 수 없을 때(XML이 아님, XFDF가 아님, 너무 큼). 앱이 사용자 알림으로 바꿔 보여 준다.</summary>
public sealed class XfdfFormatException(XfdfFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public XfdfFailure Failure { get; } = failure;
}

public enum XfdfFailure { NotValidXfdf, TooLarge }

/// <summary>
/// XFDF를 읽은 결과. Annotations: 가져올 수 있는 주석(쪽 번호는 0부터). Skipped: 건너뛴 주석 "요소 이름: 이유".
/// Notes: 건너뛰지는 않았지만 일부를 잃은 것(모르는 선 끝 이름 등). ParentNames: 파일 안에서 부모를 찾지 못한 답글이 가리키는 부모 NM.
/// </summary>
public sealed record XfdfReadResult(IReadOnlyList<(int PageIndex, Annotation A)> Annotations, IReadOnlyList<string> Skipped)
{
    internal AnnotationAssets? Assets { get; set; }
    internal Dictionary<AnnotId, (string Name, byte[] Data)> Pending { get; set; } = new();

    /// <summary>가져오기로 확정된 첨부 주석의 파일을 저장소에 넣고 AttachmentId를 채운다. 실패하면 null과 이유.</summary>
    public Annotation? StoreAttachment(Annotation a, out string? error)
    {
        error = null;
        if (a.Kind != AnnotKind.FileAttachment) return a;
        if (!Pending.TryGetValue(a.Id, out var f) || Assets is null) { error = "attachment data is missing"; return null; }
        try { return a with { AttachmentId = Assets.AddFile(f.Name, f.Data), AttachmentName = f.Name }; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { error = "attachment cannot be stored: " + e.Message; return null; }
    }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public IReadOnlyDictionary<AnnotId, string> ParentNames { get; init; } = new Dictionary<AnnotId, string>();
}

/// <summary>
/// 다른 프로그램이 만든 파일도 읽을 수 있는 XFDF 읽기 (M3b Task 6). 속성 이름은 대소문자를 가리지 않는다.
/// 보안: DTD·외부 개체 금지(XXE·XML 폭탄 차단), 파일 크기 상한, 첨부 크기는 AnnotationAssets 상한을 따른다.
/// 깨진 주석 하나는 건너뛰고 이유를 기록하며, 파일 전체가 XFDF가 아니면 <see cref="XfdfFormatException"/>만 던진다.
/// </summary>
public static partial class XfdfReader
{
    public const long MaxFileBytes = 100L * 1024 * 1024;
    public const int MaxAnnotations = 100_000;
    private const int MaxPoints = 1_000_000;
    private const int MaxNameLength = 128;
    private const int MaxElementDepth = 32;
    private const int MaxNumberChars = 16 * 1024 * 1024;
    private const long MaxTotalNumbers = 8_000_000;
    private const long MaxTotalAttachmentBytes = 200L * 1024 * 1024;
    private const int MaxTextLength = 100_000;
    private const int MaxShortTextLength = 256;

    /// <summary>한 번의 Read 동안 쓰는 예산(읽기는 한 스레드에서 동기로 끝난다).</summary>
    private sealed class Budget
    {
        public long Numbers;
        public long AttachmentBytes;
        public readonly Dictionary<AnnotId, (string Name, byte[] Data)> Pending = new();
    }
    [ThreadStatic] private static Budget? _budget;
    private static Budget B => _budget ?? throw new InvalidOperationException();

    private sealed class LimitException(string message) : Exception(message);

    public static XfdfReadResult Read(Stream xfdf, Func<AnnotId> newId, AnnotationAssets assets)
    {
        var budget = _budget = new Budget();
        try
        {
            var r = ReadCore(xfdf, newId, assets);
            r.Assets = assets;
            r.Pending = budget.Pending;
            return r;
        }
        finally { _budget = null; }
    }

    private static XfdfReadResult ReadCore(Stream xfdf, Func<AnnotId> newId, AnnotationAssets assets)
    {
        ArgumentNullException.ThrowIfNull(xfdf);
        ArgumentNullException.ThrowIfNull(newId);
        ArgumentNullException.ThrowIfNull(assets);
        if (xfdf.CanSeek && xfdf.Length > MaxFileBytes)
            throw new XfdfFormatException(XfdfFailure.TooLarge, "The XFDF file is too large.");

        var items = new List<Item>();
        var skipped = new List<string>();
        var notes = new List<string>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = 0,   // 크기는 LimitedStream이 바이트로 막는다
            MaxCharactersFromEntities = 0,
            CloseInput = false,
        };
        try
        {
            using var limited = new LimitedStream(xfdf, MaxFileBytes);
            using var xr = XmlReader.Create(limited, settings);
            xr.MoveToContent();
            if (xr.NodeType != XmlNodeType.Element || !Is(xr.LocalName, "xfdf"))
                throw new XfdfFormatException(XfdfFailure.NotValidXfdf, "The root element is not xfdf.");
            if (!xr.IsEmptyElement)
            {
                xr.Read();
                while (!xr.EOF && xr.NodeType != XmlNodeType.EndElement)
                {
                    if (xr.NodeType == XmlNodeType.Element && Is(xr.LocalName, "annots")) { if (ReadAnnots(xr, items, skipped, notes, newId, assets)) break; }
                    else if (xr.NodeType == XmlNodeType.Element) xr.Skip();
                    else xr.Read();
                }
            }
        }
        catch (XmlException e)
        {
            throw new XfdfFormatException(XfdfFailure.NotValidXfdf, "The file is not a valid XFDF document: " + e.Message, e);
        }
        catch (InvalidOperationException e)   // XmlReader가 던지는 그 밖의 형식 오류
        {
            throw new XfdfFormatException(XfdfFailure.NotValidXfdf, "The file is not a valid XFDF document: " + e.Message, e);
        }

        return Link(items, skipped, notes);
    }

    private sealed class Item
    {
        public string Element = "";
        public int Page;
        public Annotation A = null!;
        public string? ParentName;
    }

    // ---- 주석 요소 순회 ----
    private static bool ReadAnnots(XmlReader xr, List<Item> items, List<string> skipped, List<string> notes, Func<AnnotId> newId, AnnotationAssets assets)
    {
        if (xr.IsEmptyElement) { xr.Read(); return false; }
        xr.Read();
        int count = 0;
        while (!xr.EOF && xr.NodeType != XmlNodeType.EndElement)
        {
            if (xr.NodeType != XmlNodeType.Element) { xr.Read(); continue; }
            if (count >= MaxAnnotations)
            {
                skipped.Add("annots: too many annotations; the rest were ignored");
                return true;
            }
            count++;
            var el = BuildElement(xr);
            if (el is null) { skipped.Add("annots: an element is nested too deeply"); continue; }
            ReadOne(el, items, skipped, notes, newId, assets);
        }
        if (!xr.EOF && xr.NodeType == XmlNodeType.EndElement) xr.Read();
        return false;
    }

    /// <summary>
    /// 현재 요소를 트리로 만든다. 중첩이 <see cref="MaxElementDepth"/>를 넘으면 그 요소를 읽고 버린 뒤 null.
    /// XElement.Value·XNode.ReadFrom은 깊은 트리에서 재귀로 스택을 넘치게 하므로 직접 만들고 깊이를 막는다.
    /// </summary>
    private static XElement? BuildElement(XmlReader xr)
    {
        int baseDepth = xr.Depth;
        var stack = new Stack<XElement>();
        XElement? top = null, result = null;
        bool tooDeep = false;
        while (true)
        {
            switch (xr.NodeType)
            {
                case XmlNodeType.Element:
                    if (xr.Depth - baseDepth >= MaxElementDepth) { tooDeep = true; xr.Skip(); break; }
                    {
                        var el = new XElement(XName.Get(xr.LocalName));
                        bool empty = xr.IsEmptyElement;
                        if (xr.HasAttributes)
                        {
                            while (xr.MoveToNextAttribute())
                            {
                                if (xr.Prefix == "xmlns" || xr.LocalName == "xmlns" || xr.LocalName.Length == 0) continue;
                                try { el.SetAttributeValue(XName.Get(xr.LocalName), xr.Value); } catch (ArgumentException) { }
                            }
                            xr.MoveToElement();
                        }
                        if (top is not null) top.Add(el); else result = el;
                        if (!empty) { stack.Push(el); top = el; }
                        xr.Read();
                    }
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    top?.Add(new XText(xr.Value));
                    xr.Read();
                    break;
                case XmlNodeType.EndElement:
                    stack.Pop();
                    top = stack.Count > 0 ? stack.Peek() : null;
                    xr.Read();
                    break;
                default:
                    xr.Read();
                    break;
            }
            if (top is null || xr.EOF) break;
        }
        return tooDeep ? null : result;
    }

    private static void ReadOne(XElement el, List<Item> items, List<string> skipped, List<string> notes, Func<AnnotId> newId, AnnotationAssets assets)
    {
        string name = el.Name.LocalName.ToLowerInvariant();
        if (name == "popup") return;   // 말풍선 위치 정보: 우리는 만들지 않는다
        if (!Kinds.TryGetValue(name, out var kind))
        {
            skipped.Add($"{name}: unsupported element");
            return;
        }
        try
        {
            var a = Parse(el, name, kind, newId, assets, notes, out int page, out string? parent, out string? error);
            if (a is null) skipped.Add($"{name}: {error}");
            else items.Add(new Item { Element = name, Page = page, A = a, ParentName = parent });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            skipped.Add($"{name}: {e.Message}");   // 방어: 어떤 요소도 읽기 전체를 죽이지 못한다
        }
    }

    private static readonly Dictionary<string, AnnotKind> Kinds = new()
    {
        ["ink"] = AnnotKind.Ink, ["highlight"] = AnnotKind.Highlight, ["underline"] = AnnotKind.Underline,
        ["strikeout"] = AnnotKind.StrikeOut, ["text"] = AnnotKind.Text, ["square"] = AnnotKind.Square,
        ["circle"] = AnnotKind.Circle, ["line"] = AnnotKind.Line, ["polyline"] = AnnotKind.PolyLine,
        ["polygon"] = AnnotKind.Polygon, ["freetext"] = AnnotKind.FreeText, ["caret"] = AnnotKind.Caret,
        ["stamp"] = AnnotKind.Stamp, ["fileattachment"] = AnnotKind.FileAttachment,
    };

    // ---- 요소 하나 ----
    private static Annotation? Parse(XElement el, string name, AnnotKind kind, Func<AnnotId> newId, AnnotationAssets assets, List<string> notes,
        out int page, out string? parentName, out string? error)
    {
        page = 0;
        parentName = null;
        error = null;
        var at = Attrs(el);

        // 쪽 번호
        if (!at.TryGetValue("page", out var ps)) { error = "missing page number"; return null; }
        if (!int.TryParse(ps.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out page)) { error = $"page number '{Short(ps)}' is not a number"; return null; }
        if (page < 0) { error = "page number is negative"; return null; }

        // 색
        PdfColor? color = null, fill = null;
        if (at.TryGetValue("color", out var cs) && !string.IsNullOrWhiteSpace(cs))
        {
            if (!TryColor(cs, out var c)) { error = $"color '{Short(cs)}' is not in #RRGGBB format"; return null; }
            color = c;
        }
        if (at.TryGetValue("interior-color", out var ics) && !string.IsNullOrWhiteSpace(ics))
        {
            if (!TryColor(ics, out var c)) { error = $"interior color '{Short(ics)}' is not in #RRGGBB format"; return null; }
            fill = c;
        }
        color ??= DefaultColor(kind);

        // 숫자 속성
        float opacity = 1f, width = 1f;
        if (at.TryGetValue("opacity", out var os) && !string.IsNullOrWhiteSpace(os))
        {
            if (!TryFloat(os, out opacity)) { error = $"opacity '{Short(os)}' is not a number"; return null; }
            opacity = Math.Clamp(opacity, 0f, 1f);
        }
        if (at.TryGetValue("width", out var ws) && !string.IsNullOrWhiteSpace(ws))
        {
            if (!TryFloat(ws, out width) || width < 0) { error = $"width '{Short(ws)}' is not a valid number"; return null; }
            width = Math.Min(width, 1000f);
        }

        // 기하
        var quads = ImmutableArray<PdfQuadF>.Empty;
        var strokes = ImmutableArray<ImmutableArray<PdfPointF>>.Empty;
        var verts = ImmutableArray<PdfPointF>.Empty;
        switch (kind)
        {
            case AnnotKind.Highlight or AnnotKind.Underline or AnnotKind.StrikeOut:
                {
                    if (!at.TryGetValue("coords", out var co)) { error = "missing coords"; return null; }
                    var nums = Floats(co);
                    if (nums is null) { error = "coords contain a value that is not a number"; return null; }
                    if (nums.Length == 0 || nums.Length % 8 != 0 || nums.Length > MaxPoints) { error = $"coords must hold groups of 8 numbers (found {nums.Length})"; return null; }
                    var b = ImmutableArray.CreateBuilder<PdfQuadF>(nums.Length / 8);
                    for (int i = 0; i < nums.Length; i += 8)
                        b.Add(new PdfQuadF(new(nums[i], nums[i + 1]), new(nums[i + 2], nums[i + 3]), new(nums[i + 4], nums[i + 5]), new(nums[i + 6], nums[i + 7])));
                    quads = b.ToImmutable();
                    break;
                }
            case AnnotKind.Ink:
                {
                    var list = Child(el, "inklist");
                    var gestures = list?.Elements().Where(g => Is(g.Name.LocalName, "gesture")).ToList();
                    if (gestures is null || gestures.Count == 0) { error = "missing inklist"; return null; }
                    var all = ImmutableArray.CreateBuilder<ImmutableArray<PdfPointF>>(gestures.Count);
                    int total = 0;
                    foreach (var g in gestures)
                    {
                        var pts = Points(TextOf(g), out var perr);
                        if (pts is null) { error = "inklist " + perr; return null; }
                        if (pts.Value.Length == 0) continue;
                        total += pts.Value.Length;
                        if (total > MaxPoints) { error = "inklist has too many points"; return null; }
                        all.Add(pts.Value);
                    }
                    if (all.Count == 0) { error = "inklist has no points"; return null; }
                    strokes = all.ToImmutable();
                    break;
                }
            case AnnotKind.Line:
                {
                    if (!at.TryGetValue("start", out var s) || !at.TryGetValue("end", out var e)) { error = "missing start or end"; return null; }
                    var p0 = Points(s, out var e0); var p1 = Points(e, out var e1);
                    if (p0 is not { Length: 1 } || p1 is not { Length: 1 }) { error = "start and end must each be one x,y point"; return null; }
                    verts = [p0.Value[0], p1.Value[0]];
                    break;
                }
            case AnnotKind.PolyLine or AnnotKind.Polygon:
                {
                    var ve = Child(el, "vertices");
                    if (ve is null) { error = "missing vertices"; return null; }
                    var pts = Points(TextOf(ve), out var perr);
                    if (pts is null) { error = "vertices " + perr; return null; }
                    int min = kind == AnnotKind.Polygon ? 3 : 2;
                    if (pts.Value.Length < min || pts.Value.Length > MaxPoints) { error = $"vertices need at least {min} points"; return null; }
                    verts = pts.Value;
                    break;
                }
        }

        // 사각형
        PdfRectF rect;
        if (at.TryGetValue("rect", out var rs))
        {
            var rn = Floats(rs);
            if (rn is null) { error = "rect contains a value that is not a number"; return null; }
            if (rn.Length != 4) { error = $"rect must hold 4 numbers (found {rn.Length})"; return null; }
            rect = new PdfRectF(Math.Min(rn[0], rn[2]), Math.Min(rn[1], rn[3]), Math.Max(rn[0], rn[2]), Math.Max(rn[1], rn[3]));
        }
        else if (BoundsOf(quads, strokes, verts, width) is { } bounds) rect = bounds;
        else { error = "missing rect"; return null; }

        var a = new Annotation
        {
            Id = newId(),
            Kind = kind,
            Rect = rect,
            Color = color,
            FillColor = fill,
            Opacity = opacity,
            BorderWidth = width,
            Quads = quads,
            InkStrokes = strokes,
            Vertices = verts,
        };

        // 공통 문자열·날짜·표시 옵션
        string? nm = at.GetValueOrDefault("name");
        if (string.IsNullOrWhiteSpace(nm)) nm = "xfdf-" + Guid.NewGuid().ToString("N");   // 이름이 없으면 안정적인 새 NM
        else nm = Clean(nm, MaxNameLength, false);
        if (nm.Length == 0) nm = "xfdf-" + Guid.NewGuid().ToString("N");
        a = a with
        {
            Name = nm,
            Author = Nz(at.GetValueOrDefault("title")) is { } au ? Clean(au, MaxShortTextLength, false) : null,
            Subject = Nz(at.GetValueOrDefault("subject")) is { } su ? Clean(su, MaxShortTextLength, false) : null,
        };
        DateTimeOffset? mod = PdfDate.TryParse(at.GetValueOrDefault("date"), out var md) ? md : null;
        DateTimeOffset? cre = PdfDate.TryParse(at.GetValueOrDefault("creationdate"), out var cd) ? cd : null;
        a = a with { ModifiedAt = mod ?? cre, CreatedAt = cre ?? mod };

        string? contents = ContentsOf(el);
        a = a with { Contents = contents };

        // 답글·상태
        string? irt = Nz(at.GetValueOrDefault("inreplyto")) is { } irtRaw ? Clean(irtRaw, MaxNameLength, false) : null;
        string? stateStr = Nz(at.GetValueOrDefault("state"));
        string? model = Nz(at.GetValueOrDefault("statemodel"));
        ReviewState? state = null;
        if (stateStr is not null)
        {
            if (kind != AnnotKind.Text) { error = "a state is only valid on a text annotation"; return null; }
            if (model is not null && !model.Equals("Review", StringComparison.OrdinalIgnoreCase)) { error = $"state model '{Short(model)}' is not supported"; return null; }
            state = Enum.GetValues<ReviewState>().Select(s => (ReviewState?)s)
                .FirstOrDefault(s => s!.Value.ToPdfName().Equals(stateStr, StringComparison.OrdinalIgnoreCase));
            if (state is null) { error = $"state '{Short(stateStr)}' is not supported"; return null; }
            if (irt is null) { error = "a state needs inreplyto"; return null; }
        }
        bool group = at.TryGetValue("replytype", out var rt) && rt.Trim().Equals("group", StringComparison.OrdinalIgnoreCase);
        bool linkable = irt is not null && ((kind == AnnotKind.Text) || (kind == AnnotKind.Caret && group));
        if (irt is not null && !linkable)
        {
            notes.Add($"{name}: reply link '{Short(irt)}' ignored (only text replies and replace carets can be linked)");
            irt = null;
        }
        if (linkable)
        {
            parentName = irt;
            a = a with { ReplyKind = group ? ReplyType.Group : ReplyType.Reply, State = state };
        }

        // 플래그: 없으면 인쇄. 답글·상태는 기본 플래그.
        a = a with { Flags = at.TryGetValue("flags", out var fs) ? ParseFlags(fs) : DefaultFlags(linkable, state) };

        // 클라우드
        if (kind is AnnotKind.Square or AnnotKind.Circle or AnnotKind.Polygon
            && at.TryGetValue("style", out var st) && st.Trim().Equals("cloudy", StringComparison.OrdinalIgnoreCase))
        {
            float inten = 1f;
            if (at.TryGetValue("intensity", out var its) && TryFloat(its, out var iv) && iv > 0) inten = Math.Min(iv, 2f);
            a = a with { CloudIntensity = inten };
        }

        // 종류별 나머지
        switch (kind)
        {
            case AnnotKind.Text:
                break;
            case AnnotKind.Line or AnnotKind.PolyLine:
                a = WithEndings(a, at, name, notes);
                break;
            case AnnotKind.FreeText:
                {
                    var r = FreeText(a, el, at, name, notes, out error);
                    if (r is null) return null;
                    a = r;
                    break;
                }
            case AnnotKind.Stamp:
                {
                    var r = Stamp(a, at, out error);
                    if (r is null) return null;
                    a = r;
                    break;
                }
            case AnnotKind.FileAttachment:
                {
                    var r = Attachment(a, el, at, out error);
                    if (r is null) return null;
                    a = r;
                    break;
                }
        }
        return a;
    }

    private static Annotation WithEndings(Annotation a, Dictionary<string, string> at, string name, List<string> notes)
    {
        LineEnding Ending(string key)
        {
            if (!at.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) return LineEnding.None;
            var le = LineEndingExtensions.FromPdfName(v.Trim());
            if (le is null) { notes.Add($"{name}: line ending '{Short(v)}' is not supported and was replaced by none"); return LineEnding.None; }
            return le.Value;
        }
        return a with { StartEnding = Ending("head"), EndEnding = Ending("tail") };
    }

    private static Annotation? FreeText(Annotation a, XElement el, Dictionary<string, string> at, string name, List<string> notes, out string? error)
    {
        error = null;
        float size = 12f;
        PdfColor? tc = null;
        string? da = (Child(el, "defaultappearance") is { } dae ? TextOf(dae) : null) ?? at.GetValueOrDefault("defaultappearance");
        if (da is not null)
        {
            var m = FontSizeRegex().Match(da);
            if (m.Success && TryFloat(m.Groups[1].Value, out var fsz) && fsz > 0) size = Math.Min(fsz, 1000f);
            var cm = RgRegex().Match(da);
            if (cm.Success && TryFloat(cm.Groups[1].Value, out var r) && TryFloat(cm.Groups[2].Value, out var g) && TryFloat(cm.Groups[3].Value, out var b))
                tc = new PdfColor(To255(r), To255(g), To255(b));
            else if (GRegex().Match(da) is { Success: true } gm && TryFloat(gm.Groups[1].Value, out var gv))
                tc = new PdfColor(To255(gv), To255(gv), To255(gv));
        }
        var align = at.GetValueOrDefault("justification")?.Trim().ToLowerInvariant() switch
        {
            "centered" or "center" => TextAlign.Center,
            "right" => TextAlign.Right,
            _ => TextAlign.Left,
        };
        FreeTextIntent intent = FreeTextIntent.TextBox;
        string? it = at.GetValueOrDefault("intent")?.Trim();
        if (string.Equals(it, "FreeTextTypewriter", StringComparison.OrdinalIgnoreCase)) intent = FreeTextIntent.Typewriter;
        else if (string.Equals(it, "FreeTextCallout", StringComparison.OrdinalIgnoreCase)) intent = FreeTextIntent.Callout;

        var callout = ImmutableArray<PdfPointF>.Empty;
        if (at.TryGetValue("callout", out var cs) && !string.IsNullOrWhiteSpace(cs))
        {
            var nums = Floats(cs);
            if (nums is null) { error = "callout contains a value that is not a number"; return null; }
            if (nums.Length is not (4 or 6)) { error = $"callout must hold 4 or 6 numbers (found {nums.Length})"; return null; }
            callout = [.. Enumerable.Range(0, nums.Length / 2).Select(i => new PdfPointF(nums[2 * i], nums[2 * i + 1]))];
        }
        PdfRectF? box = null;
        if (at.TryGetValue("fringe", out var fr) && !string.IsNullOrWhiteSpace(fr))
        {
            var f = Floats(fr);
            if (f is null || f.Length != 4) { error = "fringe must hold 4 numbers"; return null; }
            var r = a.Rect;
            if (f.All(v => v >= 0) && r.Width - f[0] - f[2] > 0 && r.Height - f[1] - f[3] > 0)
                box = new PdfRectF(r.Left + f[0], r.Bottom + f[1], r.Right - f[2], r.Top - f[3]);
        }
        if (intent == FreeTextIntent.Callout && callout.IsDefaultOrEmpty) intent = FreeTextIntent.TextBox;   // 지시선이 없으면 설명선이 아니다
        a = a with { FontSizePt = size, TextColor = tc, Align = align, Intent = intent, Vertices = callout, TextBox = box };
        if (intent == FreeTextIntent.Callout) a = WithEndings(a, at, name, notes);
        return a;
    }

    private static Annotation? Stamp(Annotation a, Dictionary<string, string> at, out string? error)
    {
        error = null;
        string? icon = Nz(at.GetValueOrDefault("icon"));
        if (icon is not null)
        {
            if (icon.StartsWith("SB", StringComparison.Ordinal) && icon.Length > 2) icon = icon[2..];   // Acrobat 동적 스탬프 이름
            if (!StampNameRegex().IsMatch(icon)) { error = $"stamp icon '{Short(icon)}' is not a valid name"; return null; }
            return a with { Stamp = new StampSpec(StampKind.Builtin, icon, null, null) };
        }
        if (!string.IsNullOrEmpty(a.Contents))
            return a with { Stamp = new StampSpec(StampKind.Text, "", a.Contents, null), Contents = null };
        error = "stamp has neither an icon nor text";
        return null;
    }

    private static Annotation? Attachment(Annotation a, XElement el, Dictionary<string, string> at, out string? error)
    {
        error = null;
        var data = Child(el, "data");
        if (data is null) { error = "attachment has no embedded data"; return null; }
        var da = Attrs(data);
        string encoding = (da.GetValueOrDefault("encoding") ?? "base64").Trim().ToLowerInvariant();
        string text = TextOf(data);
        long encodedLen = text.Length;
        long approxBytes = encoding switch { "hex" => encodedLen / 2, "base64" => encodedLen / 4 * 3, _ => 0 };
        if (encoding is not ("hex" or "base64")) { error = $"attachment encoding '{Short(encoding)}' is not supported"; return null; }
        string? filter = Nz(da.GetValueOrDefault("filter"));
        if (filter is not null && !filter.Equals("FlateDecode", StringComparison.OrdinalIgnoreCase)) { error = $"attachment filter '{Short(filter)}' is not supported"; return null; }
        if (approxBytes > AnnotationAssets.MaxFileBytes && filter is null) { error = "attachment is larger than the allowed file size"; return null; }
        if (encodedLen > 2L * AnnotationAssets.MaxFileBytes) { error = "attachment is larger than the allowed file size"; return null; }
        byte[] bytes;
        try
        {
            bytes = encoding == "hex" ? Convert.FromHexString(StripWhitespace(text)) : Convert.FromBase64String(StripWhitespace(text));
            if (filter is not null) bytes = Inflate(bytes, AnnotationAssets.MaxFileBytes);
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException or OverflowException)
        {
            error = "attachment data is damaged";
            return null;
        }
        if (bytes.Length > AnnotationAssets.MaxFileBytes) { error = "attachment is larger than the allowed file size"; return null; }
        string fname = AttachmentNames.Sanitize(at.GetValueOrDefault("file"));
        if (B.AttachmentBytes + bytes.Length > MaxTotalAttachmentBytes) { error = "attachments exceed the total limit"; return null; }
        B.AttachmentBytes += bytes.Length;
        // 파일 저장소에는 가져오기로 확정된 주석만 넣는다(StoreAttachment). 그 전까지는 바이트만 들고 있다.
        B.Pending[a.Id] = (fname, bytes);
        return a with { AttachmentName = fname };
    }

    private static byte[] Inflate(byte[] compressed, int max)
    {
        using var input = new MemoryStream(compressed);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = z.Read(buf, 0, buf.Length)) > 0)
        {
            output.Write(buf, 0, n);
            if (output.Length > max) throw new InvalidDataException("Inflated data is too large.");
        }
        return output.ToArray();
    }

    // ---- 파일 안 답글 연결 ----
    private static XfdfReadResult Link(List<Item> items, List<string> skipped, List<string> notes)
    {
        var byName = new Dictionary<string, Item>(StringComparer.Ordinal);
        var kept = new List<Item>(items.Count);
        foreach (var it in items)
        {
            if (it.A.Name is { } n && !byName.TryAdd(n, it))
            {
                skipped.Add($"{it.Element}: duplicate name '{Short(n)}' in the file");
                B.Pending.Remove(it.A.Id);
                continue;
            }
            kept.Add(it);
        }
        var parentNames = new Dictionary<AnnotId, string>();
        var result = new List<(int, Annotation)>(kept.Count);
        foreach (var it in kept)
        {
            var a = it.A;
            if (it.ParentName is { } pn)
            {
                if (byName.TryGetValue(pn, out var parent) && parent.A.Id != a.Id) a = a with { InReplyTo = parent.A.Id };
                else parentNames[a.Id] = pn;
            }
            result.Add((it.Page, a));
        }
        return new XfdfReadResult(result, skipped) { Notes = notes, ParentNames = parentNames };
    }

    // ---- 도우미 ----
    internal static bool Is(string local, string expected) => local.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> Attrs(XElement e)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in e.Attributes())
        {
            if (a.IsNamespaceDeclaration) continue;
            d.TryAdd(a.Name.LocalName, a.Value);
        }
        return d;
    }

    private static XElement? Child(XElement e, string local)
        => e.Elements().FirstOrDefault(c => Is(c.Name.LocalName, local));

    private static string? ContentsOf(XElement el)
    {
        string? text = Child(el, "contents") is { } c ? TextOf(c) : null;
        if (string.IsNullOrEmpty(text) && Child(el, "contents-richtext") is { } rt) text = rt.Value;   // 깊이는 MaxElementDepth로 제한됨
        return string.IsNullOrEmpty(text) ? null : Clean(text, MaxTextLength, true);
    }

    /// <summary>직접 자식 글자 노드만 이어 붙인다(XElement.Value는 깊은 트리에서 재귀).</summary>
    private static string TextOf(XElement e) => string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value));

    /// <summary>제어 문자(탭·줄바꿈 제외, 줄바꿈은 multiline일 때만)를 빼고 길이를 자른다.</summary>
    private static string Clean(string s, int max, bool multiline)
    {
        var sb = new System.Text.StringBuilder(Math.Min(s.Length, max));
        foreach (char c in s)
        {
            if (sb.Length >= max) break;
            if (c == (char)9 || (multiline && (c == (char)10 || c == (char)13)) || !char.IsControl(c)) sb.Append(c);
        }
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
        return sb.ToString();
    }

    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string Short(string s) => s.Length <= 40 ? s : s[..40] + "...";

    private static bool TryFloat(string? s, out float v)
        => float.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && float.IsFinite(v);

    private static bool TryColor(string s, out PdfColor c)
    {
        c = default;
        s = s.Trim();
        if (s.Length != 7 || s[0] != '#') return false;
        try { c = PdfColor.FromHex(s); return true; }
        catch (FormatException) { return false; }
    }

    private static PdfColor? DefaultColor(AnnotKind k) => k switch
    {
        AnnotKind.Highlight => new PdfColor(255, 255, 0),
        AnnotKind.Text => new PdfColor(255, 212, 0),
        AnnotKind.Ink or AnnotKind.Underline or AnnotKind.StrikeOut or AnnotKind.Line or AnnotKind.PolyLine or AnnotKind.Caret => new PdfColor(0, 0, 0),
        _ => null,
    };

    /// <summary>쉼표·세미콜론·공백으로 나눈 숫자들. 숫자가 아닌 값이 있으면 null.</summary>
    private static float[]? Floats(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return [];
        if (s.Length > MaxNumberChars) throw new LimitException("numeric list is too long");
        var list = new List<float>();
        var span = s.AsSpan();
        int i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && IsSep(span[i])) i++;
            int start = i;
            while (i < span.Length && !IsSep(span[i])) i++;
            if (i == start) break;
            if (!float.TryParse(span[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || !float.IsFinite(v)) return null;
            if (list.Count >= MaxPoints * 2) throw new LimitException("too many numbers in one list");
            if (++B.Numbers > MaxTotalNumbers) throw new LimitException("the file has too many coordinates in total");
            list.Add(v);
        }
        return list.ToArray();
    }

    private static bool IsSep(char c) => c is (char)44 or (char)59 or (char)32 or (char)9 or (char)13 or (char)10;

    private static ImmutableArray<PdfPointF>? Points(string? s, out string error)
    {
        error = "";
        var n = Floats(s);
        if (n is null) { error = "contains a value that is not a number"; return null; }
        if (n.Length % 2 != 0) { error = $"must hold x,y pairs (found {n.Length} numbers)"; return null; }
        var b = ImmutableArray.CreateBuilder<PdfPointF>(n.Length / 2);
        for (int i = 0; i < n.Length; i += 2) b.Add(new PdfPointF(n[i], n[i + 1]));
        return b.ToImmutable();
    }

    private static PdfRectF? BoundsOf(ImmutableArray<PdfQuadF> quads, ImmutableArray<ImmutableArray<PdfPointF>> strokes, ImmutableArray<PdfPointF> verts, float width)
    {
        IEnumerable<PdfPointF> pts = quads.SelectMany(q => new[] { q.P1, q.P2, q.P3, q.P4 })
            .Concat(strokes.SelectMany(s => s)).Concat(verts);
        var list = pts.ToList();
        if (list.Count == 0) return null;
        return PdfRectF.FromPoints(list).Inflate(width / 2f);
    }

    private static int ParseFlags(string s)
    {
        int f = 0;
        foreach (var raw in s.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            f |= raw.Trim().ToLowerInvariant() switch
            {
                "invisible" => PdfAnnotFlags.Invisible,
                "hidden" => PdfAnnotFlags.Hidden,
                "print" => PdfAnnotFlags.Print,
                "nozoom" => PdfAnnotFlags.NoZoom,
                "norotate" => PdfAnnotFlags.NoRotate,
                "noview" => 32,
                "readonly" => 64,
                "locked" => 128,
                "togglenoview" => 256,
                "lockedcontents" => 512,
                _ => 0,
            };
        }
        return f;
    }

    private static int DefaultFlags(bool thread, ReviewState? state)
        => state is not null ? ThreadAnnotations.StateFlags : thread ? ThreadAnnotations.ReplyFlags : PdfAnnotFlags.Print;

    private static byte To255(float v) => (byte)Math.Clamp((int)Math.Round(v * 255f), 0, 255);

    private static string StripWhitespace(string s)
    {
        if (!s.Any(char.IsWhiteSpace)) return s;
        return string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
    }

    [GeneratedRegex(@"(-?\d+(?:\.\d+)?)\s+Tf")]
    private static partial Regex FontSizeRegex();

    [GeneratedRegex(@"(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+rg")]
    private static partial Regex RgRegex();

    [GeneratedRegex(@"(-?\d+(?:\.\d+)?)\s+g\b")]
    private static partial Regex GRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_ \-]{0,39}$")]
    private static partial Regex StampNameRegex();

    /// <summary>읽은 바이트 수가 상한을 넘으면 XfdfFormatException(TooLarge)을 던지는 스트림 덮개.</summary>
    private sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        private int Count(int n)
        {
            _read += n;
            if (_read > max) throw new XfdfFormatException(XfdfFailure.TooLarge, "The XFDF file is too large.");
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
