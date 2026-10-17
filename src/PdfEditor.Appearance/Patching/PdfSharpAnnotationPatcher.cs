using System.Text;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.Appearance.Patching;

/// <summary>
/// 패치 계획을 SaveCopy로 만든 임시 파일에 적용한다(PDFsharp 6.2.x, Modify 모드).
/// 파일만 읽고 쓰며 편집 중인 PDFium 문서는 건드리지 않는다. 계획은 상태가 없으므로 같은 입력에서 항상 같은 결과가 나온다.
/// </summary>
public sealed class PdfSharpAnnotationPatcher(Func<byte[]> fontData) : IAnnotationPatcher, IDisposable
{
    // 글꼴 바이트 공급자. AddApFont가 처음 나올 때 한 번 AppFont로 올려 두고 계속 쓴다(셰이핑은 AppFont 안에서 직렬화됨).
    private readonly Func<byte[]> _fontData = fontData;
    private readonly object _fontLock = new();
    private AppFont? _font;

    /// <summary>글꼴이 필요한데 공급자가 비어 있는 바이트를 주면 InvalidOperationException.</summary>
    private AppFont GetFont()
    {
        lock (_fontLock)
        {
            if (_font is not null) return _font;
            byte[]? data = _fontData();
            if (data is null || data.Length == 0)
                throw new InvalidOperationException("Annotation needs the Noto Sans KR font, but the font file is missing or empty (assets/fonts/NotoSansKR-Regular.otf).");
            return _font = AppFont.FromBytes(data);
        }
    }

    public void Dispose()
    {
        lock (_fontLock) { _font?.Dispose(); _font = null; }
    }

    public void Apply(string pdfPath, string? password, PatchPlan plan, AnnotationAssets assets)
    {
        if (plan.IsEmpty) return;

        // 같은 경로로 다시 저장하므로 먼저 전부 읽어 파일을 놓는다.
        byte[] bytes = File.ReadAllBytes(pdfPath);
        using var doc = PdfReader.Open(new MemoryStream(bytes), password, PdfDocumentOpenMode.Modify);

        var pending = new List<(PdfDictionary Annot, string ParentNm, string? ReplyType, int Page)>();
        var popups = new List<(int Page, PdfDictionary Parent, EnsurePopup Op)>();

        // ① Patches — 번호가 바뀌기 전
        foreach (var patch in plan.Patches)
        {
            var arr = GetAnnots(doc, patch.PageIndex, create: false)
                ?? throw new InvalidOperationException($"Page {patch.PageIndex + 1} has no annotations.");
            if (patch.AnnotIndex < 0 || patch.AnnotIndex >= arr.Elements.Count)
                throw new InvalidOperationException($"Annotation index {patch.AnnotIndex} out of range on page {patch.PageIndex + 1}.");
            var dict = Resolve(arr.Elements[patch.AnnotIndex])
                ?? throw new InvalidOperationException("Annotation entry is not a dictionary.");
            string nm = dict.Elements.GetString("/NM");
            if (nm != patch.Nm)
                throw new InvalidOperationException($"Annotation /NM mismatch: expected '{patch.Nm}', found '{nm}'.");
            if (patch.Subtype is not null && dict.Elements.GetName("/Subtype") != "/" + patch.Subtype)
                throw new InvalidOperationException($"Annotation /Subtype mismatch: expected '{patch.Subtype}'.");
            ApplyOps(doc, dict, patch.PageIndex, patch.Ops, pending, popups);
        }

        // ② Creates — 끝에 붙임
        foreach (var c in plan.Creates)
        {
            var arr = GetAnnots(doc, c.PageIndex, create: true)!;
            var dict = new PdfDictionary(doc);
            dict.Elements["/Type"] = new PdfName("/Annot");
            dict.Elements["/Subtype"] = new PdfName("/" + c.Subtype);
            dict.Elements["/NM"] = MakeString(c.Nm);
            dict.Elements["/Rect"] = Numbers(doc, c.Rect.Left, c.Rect.Bottom, c.Rect.Right, c.Rect.Top);
            dict.Elements["/F"] = new PdfInteger(c.Flags);
            doc.Internals.AddObject(dict);
            arr.Elements.Add(dict.Reference!);
            ApplyOps(doc, dict, c.PageIndex, c.Ops, pending, popups);
        }

        // /IRT는 같은 계획에서 새로 만든 부모도 가리킬 수 있으므로 Creates 뒤에 건다.
        foreach (var (annot, parentNm, replyType, page) in pending)
        {
            var parent = FindByNm(doc, page, parentNm)
                ?? throw new InvalidOperationException($"Reply parent '{parentNm}' not found on page {page + 1}.");
            if (parent.Reference is null)
                throw new InvalidOperationException("Reply parent must be an indirect object.");
            annot.Elements["/IRT"] = parent.Reference;
            if (replyType is not null) annot.Elements["/RT"] = new PdfName("/" + replyType.TrimStart('/'));
        }

        // Popup도 끝에
        foreach (var (page, parent, op) in popups)
        {
            if (parent.Elements.ContainsKey("/Popup")) continue;   // 이미 있으면 손대지 않는다
            var arr = GetAnnots(doc, page, create: true)!;
            var popup = new PdfDictionary(doc);
            popup.Elements["/Type"] = new PdfName("/Annot");
            popup.Elements["/Subtype"] = new PdfName("/Popup");
            popup.Elements["/Rect"] = Numbers(doc, op.Rect.Left, op.Rect.Bottom, op.Rect.Right, op.Rect.Top);
            popup.Elements["/Open"] = new PdfBoolean(op.Open);
            doc.Internals.AddObject(popup);
            if (parent.Reference is not null) popup.Elements["/Parent"] = parent.Reference;
            parent.Elements["/Popup"] = popup.Reference!;
            arr.Elements.Add(popup.Reference!);
        }

        // ③ Removals — 쪽마다 큰 번호부터
        foreach (var r in plan.Removals)
        {
            var arr = GetAnnots(doc, r.PageIndex, create: false);
            if (arr is null) continue;
            foreach (int idx in r.AnnotIndices.Distinct().OrderByDescending(i => i))
            {
                if (idx < 0 || idx >= arr.Elements.Count)
                    throw new InvalidOperationException($"Annotation index {idx} out of range on page {r.PageIndex + 1}.");
                arr.Elements.RemoveAt(idx);
            }
        }

        // 임시 파일에만 쓴다. 안 쓰이게 된 객체는 PDFsharp이 저장할 때 버린다.
        doc.Save(pdfPath);
    }

    // ---- 고치기 ----

    private void ApplyOps(PdfDocument doc, PdfDictionary annot, int page, IEnumerable<PatchOp> ops,
        List<(PdfDictionary, string, string?, int)> pending, List<(int, PdfDictionary, EnsurePopup)> popups)
    {
        // AP 폼을 먼저 만들고(SetAppearanceStream), 자원 추가(AddAp*)는 그 뒤에 한다 — 순서가 뒤바뀌어도 자원이 사라지지 않게.
        foreach (var op in ops.OfType<SetAppearanceStream>()) SetAppearance(doc, annot, op);

        foreach (var op in ops)
        {
            switch (op)
            {
                case SetAppearanceStream:
                    break;
                case SetName o:
                    annot.Elements[o.Key] = new PdfName(o.Value.StartsWith('/') ? o.Value : "/" + o.Value);
                    break;
                case SetNumbers o:
                    annot.Elements[o.Key] = Numbers(doc, o.Values.Select(v => (double)v).ToArray());
                    break;
                case SetNames o:
                {
                    var arr = new PdfArray(doc);
                    foreach (var n in o.Values) arr.Elements.Add(new PdfName(n.StartsWith('/') ? n : "/" + n));
                    annot.Elements[o.Key] = arr;
                    break;
                }
                case SetString o:
                    annot.Elements[o.Key] = MakeString(o.Value);
                    break;
                case SetInteger o:
                    annot.Elements[o.Key] = new PdfInteger(o.Value);
                    break;
                case SetBorderEffect o:
                {
                    var be = new PdfDictionary(doc);
                    be.Elements["/S"] = new PdfName("/C");
                    be.Elements["/I"] = new PdfReal(o.Intensity);
                    annot.Elements["/BE"] = be;
                    break;
                }
                case RemoveKeys o:
                    foreach (var k in o.Keys) annot.Elements.Remove(k);
                    break;
                case EnsurePopup o:
                    popups.Add((page, annot, o));
                    break;
                case SetInReplyTo o:
                    pending.Add((annot, o.ParentNm, o.ReplyType, page));
                    break;
                case AddApFont o:
                    AddApFontResource(doc, annot, o);
                    break;
                case AddApImage:
                case EmbedFile:
                    throw new NotSupportedException($"{op.GetType().Name} is not implemented yet (later M3a task).");
                default:
                    throw new NotSupportedException($"Unknown patch op {op.GetType().Name}.");
            }
        }
    }

    private static void SetAppearance(PdfDocument doc, PdfDictionary annot, SetAppearanceStream op)
    {
        // 기존 /AP /N의 /Resources(PDFium이 넣은 /ExtGState 등)를 새 폼으로 합친다.
        var resources = new PdfDictionary(doc);
        if (Resolve(annot.Elements["/AP"]) is { } oldAp
            && Resolve(oldAp.Elements["/N"]) is { } oldForm
            && Resolve(oldForm.Elements["/Resources"]) is { } oldRes)
        {
            foreach (var key in oldRes.Elements.Keys) resources.Elements[key] = oldRes.Elements[key]!;
        }

        var form = new PdfDictionary(doc);
        form.Elements["/Type"] = new PdfName("/XObject");
        form.Elements["/Subtype"] = new PdfName("/Form");
        form.Elements["/BBox"] = Numbers(doc, op.BBox.Left, op.BBox.Bottom, op.BBox.Right, op.BBox.Top);
        form.Elements["/Resources"] = resources;
        form.CreateStream(Encoding.Latin1.GetBytes(op.Content));
        doc.Internals.AddObject(form);

        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = form.Reference!;
        annot.Elements["/AP"] = ap;
    }

    /// <summary>AP 폼의 /Resources /Font /이름 에 글꼴(Type0 서브셋)을 넣는다. 이미 있는 자원(/ExtGState 등)과 다른 글꼴은 그대로 둔다.</summary>
    private void AddApFontResource(PdfDocument doc, PdfDictionary annot, AddApFont op)
    {
        var form = Resolve(Resolve(annot.Elements["/AP"])?.Elements["/N"])
            ?? throw new InvalidOperationException("AddApFont needs an annotation appearance stream (/AP /N).");
        var fontRef = FontEmbedder.Embed(doc, GetFont(), op.Font);

        var resources = Resolve(form.Elements["/Resources"]);
        if (resources is null)
        {
            resources = new PdfDictionary(doc);
            form.Elements["/Resources"] = resources;
        }
        var fonts = Resolve(resources.Elements["/Font"]);
        if (fonts is null)
        {
            fonts = new PdfDictionary(doc);
            resources.Elements["/Font"] = fonts;
        }
        fonts.Elements[op.ResourceName.StartsWith('/') ? op.ResourceName : "/" + op.ResourceName] = fontRef;
    }

    // ---- 도우미 ----

    private static PdfArray? GetAnnots(PdfDocument doc, int pageIndex, bool create)
    {
        if (pageIndex < 0 || pageIndex >= doc.PageCount)
            throw new InvalidOperationException($"Page index {pageIndex} out of range.");
        var page = doc.Pages[pageIndex];
        var item = page.Elements["/Annots"];
        var arr = item switch
        {
            PdfReference r => r.Value as PdfArray,
            PdfArray a => a,
            _ => null,
        };
        if (arr is null && create)
        {
            arr = new PdfArray(doc);
            page.Elements["/Annots"] = arr;
        }
        return arr;
    }

    private static PdfDictionary? Resolve(PdfItem? item) => item switch
    {
        PdfReference r => r.Value as PdfDictionary,
        PdfDictionary d => d,
        _ => null,
    };

    private static PdfDictionary? FindByNm(PdfDocument doc, int pageIndex, string nm)
    {
        var arr = GetAnnots(doc, pageIndex, create: false);
        if (arr is null) return null;
        foreach (var item in arr.Elements)
            if (Resolve(item) is { } d && d.Elements.GetString("/NM") == nm) return d;
        return null;
    }

    private static PdfArray Numbers(PdfDocument doc, params double[] values)
    {
        var arr = new PdfArray(doc);
        foreach (var v in values) arr.Elements.Add(new PdfReal(v));
        return arr;
    }

    private static PdfString MakeString(string value)
        => new(value, value.All(c => c < 0x80) ? PdfStringEncoding.RawEncoding : PdfStringEncoding.Unicode);
}
