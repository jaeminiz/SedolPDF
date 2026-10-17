using System.Collections.Immutable;
using Pdf.Native;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Annotations.Reading;

/// <summary>
/// 쪽의 /Annots를 PDFsharp로 읽어 편집 가능 여부를 판정한다 (읽기 전용, PDFium을 쓰지 않으므로 어느 스레드에서 불러도 된다).
/// 결과 목록의 순서와 Index는 /Annots 배열 순서이며 PDFium 주석 번호와 같다.
/// </summary>
public static class AnnotationReader
{
    private static int _openCount;

    /// <summary>테스트용: 문서를 열 때마다 1 증가.</summary>
    internal static int OpenCount => Volatile.Read(ref _openCount);

    /// <summary>테스트용: null이 아니면 Read가 이 예외를 감싸 던진다.</summary>
    internal static Exception? TestFailure { get; set; }

    private static readonly Dictionary<string, int> SubtypeByName = new(StringComparer.Ordinal)
    {
        ["/Text"] = PdfAnnotSubtype.Text, ["/Link"] = PdfAnnotSubtype.Link, ["/FreeText"] = PdfAnnotSubtype.FreeText,
        ["/Line"] = PdfAnnotSubtype.Line, ["/Square"] = PdfAnnotSubtype.Square, ["/Circle"] = PdfAnnotSubtype.Circle,
        ["/Polygon"] = PdfAnnotSubtype.Polygon, ["/PolyLine"] = PdfAnnotSubtype.PolyLine,
        ["/Highlight"] = PdfAnnotSubtype.Highlight, ["/Underline"] = PdfAnnotSubtype.Underline,
        ["/Squiggly"] = PdfAnnotSubtype.Squiggly, ["/StrikeOut"] = PdfAnnotSubtype.StrikeOut,
        ["/Stamp"] = PdfAnnotSubtype.Stamp, ["/Caret"] = PdfAnnotSubtype.Caret, ["/Ink"] = PdfAnnotSubtype.Ink,
        ["/Popup"] = PdfAnnotSubtype.Popup, ["/FileAttachment"] = PdfAnnotSubtype.FileAttachment,
        ["/Widget"] = PdfAnnotSubtype.Widget,
    };

    /// <summary>요청한 쪽마다 /Annots 순서 그대로의 목록. 문서를 열 수 없으면 AnnotationReadException.</summary>
    public static IReadOnlyList<IReadOnlyList<OriginalAnnotation>> Read(byte[] pdf, string? password, IReadOnlyList<int> pageIndices)
    {
        try
        {
            if (TestFailure is { } injected) throw injected;
            using var ms = new MemoryStream(pdf, writable: false);
            // 사용자 암호 문서가 Import 모드로 열리는지는 결과 문서(M2 Task 3)에 실측을 적었다.
            using var doc = password is null
                ? PdfReader.Open(ms, PdfDocumentOpenMode.Import)
                : PdfReader.Open(ms, password, PdfDocumentOpenMode.Import);
            Interlocked.Increment(ref _openCount);

            var result = new List<IReadOnlyList<OriginalAnnotation>>(pageIndices.Count);
            foreach (int pageIndex in pageIndices)
                result.Add(ReadPage(doc.Pages[pageIndex]));
            return result;
        }
        catch (Exception e)
        {
            throw new AnnotationReadException("Cannot read the original annotations.", e);
        }
    }

    /// <summary>
    /// 원본 파일 첨부 주석의 첨부 파일을 꺼낸다(/FS /EF /UF 또는 /F). 그 번호가 파일 첨부 주석이 아니거나 첨부 스트림이 없으면 null.
    /// 문서를 열 수 없으면 AnnotationReadException.
    /// </summary>
    public static (string Name, byte[] Data)? ReadAttachment(byte[] pdf, string? password, int page, int index)
    {
        try
        {
            using var ms = new MemoryStream(pdf, writable: false);
            using var doc = password is null
                ? PdfReader.Open(ms, PdfDocumentOpenMode.Import)
                : PdfReader.Open(ms, password, PdfDocumentOpenMode.Import);
            var annots = doc.Pages[page].Elements.GetArray("/Annots");
            if (annots is null || index < 0 || index >= annots.Elements.Count) return null;
            if (Resolve(annots.Elements[index]) is not PdfDictionary annot || annot.Elements.GetName("/Subtype") != "/FileAttachment") return null;
            if (Resolve(annot.Elements["/FS"]) is not PdfDictionary fs) return null;
            if (Resolve(fs.Elements["/EF"]) is not PdfDictionary ef) return null;
            var file = Resolve(ef.Elements["/UF"]) as PdfDictionary ?? Resolve(ef.Elements["/F"]) as PdfDictionary;
            if (file?.Stream is not { } stream) return null;
            string name = AttachmentNames.Sanitize(Str(fs.Elements, "/UF") ?? Str(fs.Elements, "/F"));
            var data = ReadBounded(file, stream);
            return data is null ? null : (name, data);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new AnnotationReadException("Cannot read the attached file.", e);
        }
    }

    /// <summary>
    /// 첨부 스트림 내용. 선언된 /Params /Size와 실제 길이를 상한(AnnotationAssets.MaxFileBytes)과 먼저 비교하고,
    /// Flate는 상한까지만 풀어(압축 폭탄 방어) 넘으면 null. 압축 없음은 그대로, 그 밖의 필터는 안전하게 풀 수 없어 null.
    /// </summary>
    private static byte[]? ReadBounded(PdfDictionary file, PdfDictionary.PdfStream stream)
    {
        const int cap = AnnotationAssets.MaxFileBytes;
        if (Resolve(file.Elements["/Params"]) is PdfDictionary pars && NumItem(pars.Elements["/Size"]) is { } declared && (declared < 0 || declared > cap))
            return null;
        byte[] raw = stream.Value;
        string filter = Resolve(file.Elements["/Filter"]) switch
        {
            PdfName n => n.Value,
            PdfArray { Elements.Count: 1 } a when Resolve(a.Elements[0]) is PdfName n1 => n1.Value,
            null => "",
            _ => "?",
        };
        if (filter == "")
            return raw.Length > cap ? null : raw;
        if (filter != "/FlateDecode" || file.Elements.ContainsKey("/DecodeParms") || raw.Length > cap) return null;
        using var inflate = new System.IO.Compression.ZLibStream(new MemoryStream(raw, writable: false), System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buf = new byte[81920];
        int n2;
        while ((n2 = inflate.Read(buf, 0, buf.Length)) > 0)
        {
            if (output.Length + n2 > cap) return null;
            output.Write(buf, 0, n2);
        }
        return output.ToArray();
    }

    private static List<OriginalAnnotation> ReadPage(PdfSharp.Pdf.PdfPage page)
    {
        var list = new List<OriginalAnnotation>();
        var annots = page.Elements.GetArray("/Annots");
        if (annots is null) return list;

        int i = 0;
        foreach (var item in annots.Elements)
        {
            list.Add(ReadOne(i, item));
            i++;
        }
        return list;
    }

    private static OriginalAnnotation ReadOne(int index, PdfItem? item)
    {
        int subtype = 0;
        try
        {
            if (Resolve(item) is not PdfDictionary dict)
                return new OriginalAnnotation(index, 0, null, ReadOnlyReason.Malformed);

            var e = dict.Elements;
            string? name = e.GetName("/Subtype");
            if (name is not null && SubtypeByName.TryGetValue(name, out int st)) subtype = st;
            var kind = AnnotKindExtensions.FromPdfSubtype(subtype);

            if (subtype is PdfAnnotSubtype.Stamp or PdfAnnotSubtype.FileAttachment) return Fail(ReadOnlyReason.OriginalAppearanceOnly);
            if (kind is null) return Fail(ReadOnlyReason.UnsupportedType);
            if (e.ContainsKey("/IRT")) return Fail(ReadOnlyReason.Reply);
            if (e.ContainsKey("/State")) return Fail(ReadOnlyReason.ReviewState);
            // 클라우드 테두리(/BE /S /C, /I > 0)는 Square·Polygon에서 편집할 수 있다(A8). /I가 없거나 0 이하면 클라우드가 아니다(ISO 기본값 0).
            // Circle 클라우드는 Painter가 그리지 못하므로 읽기 전용이다.
            float cloud = 0f;
            bool cloudBe = false;
            if (e.GetDictionary("/BE") is { } be && be.Elements.GetName("/S") == "/C" && (Num(be.Elements, "/I") ?? 0.0) > 0.0)
            {
                if (kind == AnnotKind.Circle) return Fail(ReadOnlyReason.CloudBorder);
                cloudBe = true;
                cloud = Math.Clamp((float)Num(be.Elements, "/I")!.Value, 0.01f, 2f);
            }
            if (kind is AnnotKind.Line or AnnotKind.PolyLine or AnnotKind.Polygon && HasLineFeatures(e, kind.Value))
                return Fail(ReadOnlyReason.UnsupportedLineFeatures);

            var bs = e.GetDictionary("/BS");
            if (bs is not null)
            {
                string? style = bs.Elements.GetName("/S");
                if ((!string.IsNullOrEmpty(style) && style != "/S") || bs.Elements.ContainsKey("/D"))
                    return Fail(ReadOnlyReason.StyledBorder);
            }

            // /RD: 일반 사각형·타원에서는 모양이 달라지므로 읽기 전용. 클라우드 Square는 /RD가 클라우드 안쪽 간격이라 허용한다.
            if (kind is AnnotKind.Square or AnnotKind.Circle && !cloudBe && HasNonZeroRd(e.GetArray("/RD")))
                return Fail(ReadOnlyReason.RectDifferences);

            int flags = Int(e, "/F") ?? 0;
            if ((flags & PdfAnnotFlags.Hidden) != 0) return Fail(ReadOnlyReason.HiddenFlag);

            var rect = ReadRect(e.GetArray("/Rect"));
            if (rect is null) return Fail(ReadOnlyReason.Malformed);

            var strokes = ImmutableArray<ImmutableArray<PdfPointF>>.Empty;
            if (kind == AnnotKind.Ink)
            {
                var s = ReadInk(e.GetArray("/InkList"));
                if (s is null) return Fail(ReadOnlyReason.Malformed);
                strokes = s.Value;
            }

            var quads = ImmutableArray<PdfQuadF>.Empty;
            if (kind.Value.IsTextMarkup())
            {
                var q = ReadQuads(e.GetArray("/QuadPoints"));
                if (q is null) return Fail(ReadOnlyReason.Malformed);
                quads = q.Value;
            }

            PdfColor? color = e.ContainsKey("/C")
                ? ReadColor(e.GetArray("/C"))
                : (kind == AnnotKind.Highlight ? PdfColor.FromHex("#FFFF00") : kind == AnnotKind.FreeText ? null : PdfColor.FromHex("#000000"));
            PdfColor? fill = e.ContainsKey("/IC") ? ReadColor(e.GetArray("/IC")) : null;

            var ft = kind == AnnotKind.FreeText ? ReadFreeText(e, rect.Value) : null;
            if (ft is { Failure: not ReadOnlyReason.None }) return Fail(ft.Failure);

            var shape = kind is AnnotKind.Line or AnnotKind.PolyLine or AnnotKind.Polygon ? ReadShape(e, kind.Value) : null;
            if (shape is { Failure: not ReadOnlyReason.None }) return Fail(shape.Failure);

            var data = new Annotation
            {
                Kind = kind.Value,
                Rect = ft?.Rect ?? rect.Value,
                CloudIntensity = cloud,
                StartEnding = shape?.Start ?? LineEnding.None,
                Color = color,
                FillColor = fill,
                Opacity = (float)(Num(e, "/CA") ?? 1.0),
                BorderWidth = ReadBorderWidth(e, bs),
                InkStrokes = strokes,
                Quads = quads,
                Contents = Str(e, "/Contents"),
                Author = Str(e, "/T"),
                Subject = Str(e, "/Subj"),
                Name = Str(e, "/NM"),
                ModifiedAt = Date(Str(e, "/M")),
                CreatedAt = Date(Str(e, "/CreationDate")),
                Flags = flags,
                Intent = ft?.Intent,
                TextBox = ft?.TextBox,
                Vertices = ft?.Vertices ?? shape?.Vertices ?? [],
                EndEnding = ft?.EndEnding ?? shape?.End ?? LineEnding.None,
                FontSizePt = ft?.FontSizePt ?? 12f,
                TextColor = ft?.TextColor,
                Align = ft?.Align ?? TextAlign.Left,
            };
            return new OriginalAnnotation(index, subtype, data, ReadOnlyReason.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new OriginalAnnotation(index, subtype, null, ReadOnlyReason.Malformed);
        }

        OriginalAnnotation Fail(ReadOnlyReason r) => new(index, subtype, null, r);
    }

    /// <summary>
    /// 선의 지시선(/LL /LLE /LLO)·캡션(/Cap /CP /CO)·/Measure 중 의미 있는 값이 있는가. 우리 모델·Painter는 이를 그리지도 지우지도 못하므로
    /// 손대면 정보가 사라진다 → 읽기 전용. /Measure는 세 유형 모두, 나머지는 Line만 해당.
    /// </summary>
    private static bool HasLineFeatures(PdfDictionary.DictionaryElements e, AnnotKind kind)
    {
        if (Resolve(e["/Measure"]) is not null) return true;
        if (kind != AnnotKind.Line) return false;
        foreach (var k in new[] { "/LL", "/LLE", "/LLO" })
            if (Num(e, k) is { } v && v != 0) return true;
        if (Resolve(e["/Cap"]) is PdfBoolean { Value: true }) return true;
        var co = Numbers(Resolve(e["/CO"]) as PdfArray);
        return co is not null && co.Any(v => v != 0);
    }

    private sealed record ShapeData(ReadOnlyReason Failure, ImmutableArray<PdfPointF> Vertices, LineEnding Start, LineEnding End);

    /// <summary>Line(/L, /LE), PolyLine·Polygon(/Vertices; PolyLine은 /LE). 모양이 망가지면 Malformed, 모르는 선 끝 이름은 UnsupportedLineEnding.</summary>
    private static ShapeData ReadShape(PdfDictionary.DictionaryElements e, AnnotKind kind)
    {
        ShapeData Bad(ReadOnlyReason r) => new(r, [], LineEnding.None, LineEnding.None);

        var n = Numbers(e.GetArray(kind == AnnotKind.Line ? "/L" : "/Vertices"));
        int min = kind switch { AnnotKind.Line => 2, AnnotKind.PolyLine => 2, _ => 3 };
        if (n is null || n.Length % 2 != 0 || n.Length / 2 < min || (kind == AnnotKind.Line && n.Length != 4))
            return Bad(ReadOnlyReason.Malformed);
        var verts = ImmutableArray.CreateBuilder<PdfPointF>(n.Length / 2);
        for (int i = 0; i < n.Length; i += 2) verts.Add(new PdfPointF((float)n[i], (float)n[i + 1]));

        LineEnding start = LineEnding.None, end = LineEnding.None;
        if (kind != AnnotKind.Polygon)
        {
            // /LE: 이름 둘의 배열(시작, 끝). 이름 하나만 있으면 시작 끝 모양으로 본다.
            var names = new List<string>();
            switch (Resolve(e["/LE"]))
            {
                case PdfName single: names.Add(single.Value); break;
                case PdfArray arr:
                    foreach (var item in arr.Elements)
                    {
                        if (Resolve(item) is not PdfName nm) return Bad(ReadOnlyReason.UnsupportedLineEnding);
                        names.Add(nm.Value);
                    }
                    break;
            }
            LineEnding? Parse(int i)
                => i < names.Count ? LineEndingExtensions.FromPdfName(names[i]) : LineEnding.None;
            var s = Parse(0);
            var t = Parse(1);
            if (s is null || t is null || names.Count > 2) return Bad(ReadOnlyReason.UnsupportedLineEnding);
            start = s.Value;
            end = t.Value;
        }
        return new ShapeData(ReadOnlyReason.None, verts.ToImmutable(), start, end);
    }

    private sealed record FreeTextData(
        ReadOnlyReason Failure, PdfRectF Rect, FreeTextIntent Intent, PdfRectF? TextBox, ImmutableArray<PdfPointF> Vertices,
        LineEnding EndEnding, float FontSizePt, PdfColor? TextColor, TextAlign Align);

    /// <summary>FreeText 전용 값: /IT, /DA, /Q, /CL, /RD, /LE. 지원하지 않는 /IT·/LE는 UnsupportedType, 모양이 망가진 /CL은 Malformed.</summary>
    private static FreeTextData ReadFreeText(PdfDictionary.DictionaryElements e, PdfRectF rect)
    {
        FreeTextData Bad(ReadOnlyReason r) => new(r, rect, FreeTextIntent.TextBox, null, [], LineEnding.None, 12f, null, TextAlign.Left);

        FreeTextIntent intent;
        switch (e.GetName("/IT"))
        {
            case null or "": intent = FreeTextIntent.TextBox; break;
            case "/FreeTextTypeWriter": intent = FreeTextIntent.Typewriter; break;
            case "/FreeTextCallout": intent = FreeTextIntent.Callout; break;
            default: return Bad(ReadOnlyReason.UnsupportedType);
        }

        var (size, color) = DefaultAppearance.Parse(Str(e, "/DA"));
        float fontSize = size is > 0f ? size.Value : 12f;   // 0 = 자동 크기: 12로 둔다
        int q = Int(e, "/Q") ?? 0;
        var align = q == 1 ? TextAlign.Center : q == 2 ? TextAlign.Right : TextAlign.Left;

        // /RD: Rect에서 안쪽 글 상자까지의 차이(왼쪽, 아래, 오른쪽, 위).
        PdfRectF inner = rect;
        var rd = Numbers(e.GetArray("/RD"));
        if (rd is { Length: 4 } && rd.All(v => v >= 0))
        {
            var r = new PdfRectF((float)(rect.Left + rd[0]), (float)(rect.Bottom + rd[1]), (float)(rect.Right - rd[2]), (float)(rect.Top - rd[3]));
            if (r.Width > 0 && r.Height > 0) inner = r;
        }

        var verts = ImmutableArray<PdfPointF>.Empty;
        var ending = LineEnding.None;
        if (intent == FreeTextIntent.Callout)
        {
            var cl = Numbers(e.GetArray("/CL"));
            if (cl is null || (cl.Length != 4 && cl.Length != 6)) return Bad(ReadOnlyReason.Malformed);
            var b = ImmutableArray.CreateBuilder<PdfPointF>(cl.Length / 2);
            for (int i = 0; i < cl.Length; i += 2) b.Add(new PdfPointF((float)cl[i], (float)cl[i + 1]));
            verts = b.ToImmutable();

            // /LE: ISO 32000은 이름 하나, 일부 프로그램은 배열로 쓴다(첫 항목이 지시선 시작점).
            string? le = Resolve(e["/LE"]) switch
            {
                PdfName n => n.Value,
                PdfArray a when a.Elements.Count > 0 => (Resolve(a.Elements[0]) as PdfName)?.Value,
                _ => null,
            };
            if (le is not null)
            {
                if (LineEndingExtensions.FromPdfName(le) is not { } parsed) return Bad(ReadOnlyReason.UnsupportedType);
                ending = parsed;
            }
        }

        // 설명선은 Rect 전체(지시선 포함)와 글 상자를 따로 갖는다. 나머지는 안쪽 상자가 곧 Rect다.
        return intent == FreeTextIntent.Callout
            ? new FreeTextData(ReadOnlyReason.None, rect, intent, inner, verts, ending, fontSize, color, align)
            : new FreeTextData(ReadOnlyReason.None, inner, intent, null, verts, ending, fontSize, color, align);
    }

    private static PdfItem? Resolve(PdfItem? item)
    {
        // 참조는 가리키는 객체로, null 객체(PdfNull)는 null로 푼다.
        var v = item is PdfReference r ? r.Value : item;
        return v is PdfNull ? null : v;
    }

    private static double? NumItem(PdfItem? item) => Resolve(item) switch
    {
        PdfInteger i => i.Value,
        PdfReal d => d.Value,
        _ => null,
    };

    private static double? Num(PdfDictionary.DictionaryElements e, string key) => NumItem(e[key]);

    private static int? Int(PdfDictionary.DictionaryElements e, string key) => Num(e, key) is { } d ? (int)d : null;

    private static string? Str(PdfDictionary.DictionaryElements e, string key)
        => Resolve(e[key]) is PdfString s ? s.Value : null;

    private static DateTimeOffset? Date(string? s)
        => PdfDate.TryParse(s, out var t) ? t : null;

    private static double[]? Numbers(PdfArray? a)
    {
        if (a is null) return null;
        var r = new double[a.Elements.Count];
        for (int i = 0; i < r.Length; i++)
        {
            if (NumItem(a.Elements[i]) is not { } v) return null;
            r[i] = v;
        }
        return r;
    }

    private static bool HasNonZeroRd(PdfArray? rd)
    {
        var n = Numbers(rd);
        return n is not null && n.Any(v => v != 0);
    }

    private static PdfRectF? ReadRect(PdfArray? a)
    {
        var n = Numbers(a);
        if (n is null || n.Length != 4) return null;
        return new PdfRectF(
            (float)Math.Min(n[0], n[2]), (float)Math.Min(n[1], n[3]),
            (float)Math.Max(n[0], n[2]), (float)Math.Max(n[1], n[3]));
    }

    private static ImmutableArray<ImmutableArray<PdfPointF>>? ReadInk(PdfArray? list)
    {
        if (list is null) return null;
        var strokes = ImmutableArray.CreateBuilder<ImmutableArray<PdfPointF>>();
        foreach (var item in list.Elements)
        {
            var n = Numbers(Resolve(item) as PdfArray);
            if (n is null || n.Length < 2) return null;
            var pts = ImmutableArray.CreateBuilder<PdfPointF>(n.Length / 2);
            for (int i = 0; i + 1 < n.Length; i += 2) pts.Add(new PdfPointF((float)n[i], (float)n[i + 1]));
            strokes.Add(pts.ToImmutable());
        }
        return strokes.Count == 0 ? null : strokes.ToImmutable();
    }

    private static ImmutableArray<PdfQuadF>? ReadQuads(PdfArray? a)
    {
        var n = Numbers(a);
        if (n is null || n.Length == 0 || n.Length % 8 != 0) return null;
        var quads = ImmutableArray.CreateBuilder<PdfQuadF>(n.Length / 8);
        for (int i = 0; i < n.Length; i += 8)
            quads.Add(new PdfQuadF(
                new PdfPointF((float)n[i], (float)n[i + 1]), new PdfPointF((float)n[i + 2], (float)n[i + 3]),
                new PdfPointF((float)n[i + 4], (float)n[i + 5]), new PdfPointF((float)n[i + 6], (float)n[i + 7])));
        return quads.ToImmutable();
    }

    private static PdfColor? ReadColor(PdfArray? a)
    {
        var n = Numbers(a);
        if (n is null || n.Length == 0) return null;
        switch (n.Length)
        {
            case 1: return new PdfColor(B(n[0]), B(n[0]), B(n[0]));
            case 3: return new PdfColor(B(n[0]), B(n[1]), B(n[2]));
            case 4:
                {
                    double k = 1 - Clamp01(n[3]);
                    return new PdfColor(B((1 - Clamp01(n[0])) * k), B((1 - Clamp01(n[1])) * k), B((1 - Clamp01(n[2])) * k));
                }
            default: return null;
        }
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    private static byte B(double v) => (byte)Math.Round(Clamp01(v) * 255, MidpointRounding.AwayFromZero);

    private static float ReadBorderWidth(PdfDictionary.DictionaryElements e, PdfDictionary? bs)
    {
        if (bs is not null && Num(bs.Elements, "/W") is { } w) return (float)w;
        if (e.GetArray("/Border") is { } b && b.Elements.Count >= 3 && NumItem(b.Elements[2]) is { } bw) return (float)bw;
        return 1f;
    }
}
