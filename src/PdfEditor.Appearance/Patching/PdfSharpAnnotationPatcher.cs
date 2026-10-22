using System.Collections.Immutable;
using System.Text;
using PdfEditor.Appearance.Fonts;
using PdfEditor.ContentStream;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Output;
using PdfEditor.Document.Annotations.Patching;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.Appearance.Patching;

/// <summary>
/// 패치 계획을 SaveCopy로 만든 임시 파일에 적용한다(PDFsharp 6.2.x, Modify 모드).
/// 파일만 읽고 쓰며 편집 중인 PDFium 문서는 건드리지 않는다. 계획은 상태가 없으므로 같은 입력에서 항상 같은 결과가 나온다.
/// </summary>
public sealed class PdfSharpAnnotationPatcher(Func<byte[]> fontData) : IAnnotationPatcher, IContentRewriteProbe, IContentRewriteReport, IDisposable
{
    // 글꼴 바이트 공급자. AddApFont가 처음 나올 때 한 번 AppFont로 올려 두고 계속 쓴다(셰이핑은 AppFont 안에서 직렬화됨).
    private readonly Func<byte[]> _fontData = fontData;
    private readonly object _fontLock = new();
    private AppFont? _font;
    private bool _ownsFont = true;
    private Func<AppFont?>? _sharedFont;   // 앱이 공유하는 AppFont를 처음 필요할 때 받아 온다(null이면 글꼴 없음)

    /// <summary>앱이 화면·AP와 함께 쓰는 AppFont 하나를 처음 필요할 때(글자가 있는 주석을 저장할 때) 공급자에서 받아 쓴다. 이 패처는 그 글꼴을 닫지 않는다.</summary>
    public static PdfSharpAnnotationPatcher Shared(Func<AppFont?> font) => new(() => []) { _sharedFont = font, _ownsFont = false };

    /// <summary>앱이 화면·AP와 함께 쓰는 AppFont 하나를 공유한다. 이 패처는 그 글꼴을 닫지 않는다.</summary>
    public PdfSharpAnnotationPatcher(AppFont font) : this(() => font.Data)
    {
        _font = font;
        _ownsFont = false;
    }

    /// <summary>글꼴이 필요한데 공급자가 비어 있는 바이트를 주면 InvalidOperationException.</summary>
    private AppFont GetFont()
    {
        lock (_fontLock)
        {
            if (_font is not null) return _font;
            if (_sharedFont is not null)
                return _font = _sharedFont() ?? throw new InvalidOperationException("Annotation needs the Noto Sans KR font, but the font file is missing or empty (assets/fonts/NotoSansKR-Regular.otf).");
            byte[]? data = _fontData();
            if (data is null || data.Length == 0)
                throw new InvalidOperationException("Annotation needs the Noto Sans KR font, but the font file is missing or empty (assets/fonts/NotoSansKR-Regular.otf).");
            return _font = AppFont.FromBytes(data);
        }
    }

    public void Dispose()
    {
        lock (_fontLock) { if (_ownsFont) _font?.Dispose(); _font = null; }
    }

    /// <summary>가장 최근 Apply에서 숨김으로 남은 원본 글 객체 수(C12).</summary>
    public int HiddenTextCount { get; private set; }

    public void Apply(string pdfPath, string? password, PatchPlan plan, AnnotationAssets assets)
    {
        HiddenTextCount = 0;
        if (plan.IsEmpty) return;

        // 같은 경로로 다시 저장하므로 먼저 전부 읽어 파일을 놓는다.
        byte[] bytes = File.ReadAllBytes(pdfPath);
        using var doc = PdfReader.Open(new MemoryStream(bytes), password, PdfDocumentOpenMode.Modify);

        var pending = new List<(PdfDictionary Annot, string ParentNm, string? ReplyType, int Page)>();
        var popups = new List<(int Page, PdfDictionary Parent, EnsurePopup Op)>();
        var images = new Dictionary<string, PdfReference>(StringComparer.Ordinal);   // 이미지 XObject는 문서마다 id당 한 번만 만든다

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
            ApplyOps(doc, dict, patch.PageIndex, patch.Ops, pending, popups, assets, images);
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
            ApplyOps(doc, dict, c.PageIndex, c.Ops, pending, popups, assets, images);
        }

        // /IRT는 같은 계획에서 새로 만든 부모도 가리킬 수 있으므로 Creates 뒤에 건다.
        foreach (var (annot, parentNm, replyType, page) in pending)
        {
            var parent = FindByNm(doc, page, parentNm)
                ?? throw new InvalidOperationException($"Reply parent '{parentNm}' not found on page {page + 1}.");
            if (parent.Reference is null)
            {
                // PDFium이 저장한 주석은 /Annots 안에 직접 객체로 있을 수 있다: 간접 객체로 올리고 배열 항목을 참조로 바꾼다.
                var arr = GetAnnots(doc, page, create: false)!;
                int at = arr.Elements.ToList().FindIndex(x => ReferenceEquals(Resolve(x), parent));
                if (at < 0) throw new InvalidOperationException("Reply parent must be an indirect object.");
                doc.Internals.AddObject(parent);
                arr.Elements[at] = parent.Reference!;
            }
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

        // ④ 쪽 내용 덧붙이기(M4 앱 글꼴 줄) — 주석과 무관
        if (!plan.ContentAppends.IsDefaultOrEmpty) AppendPageContents(doc, plan.ContentAppends);

        // ⑤ 객체 편집을 내용 스트림 직접 고쳐 쓰기로 확정(M4x) — PDFium이 못 다시 쓰는 쪽만
        if (!plan.ContentRewrites.IsDefaultOrEmpty) ApplyContentRewrites(doc, plan.ContentRewrites, assets);

        // 임시 파일에만 쓴다. 안 쓰이게 된 객체는 PDFsharp이 저장할 때 버린다.
        doc.Save(pdfPath);
    }

    // ---- 쪽 내용 덧붙이기 (M4) ----

    /// <summary>
    /// 쪽마다 기존 내용을 q … Q로 감싼 뒤(앞 내용이 남긴 행렬·색이 새 글자에 번지지 않게) 새 스트림을 붙이고,
    /// 글꼴 서브셋을 쪽 /Resources /Font에 겹치지 않는 이름(SedolF1, SedolF2 …)으로 넣는다.
    /// </summary>
    private void AppendPageContents(PdfDocument doc, IEnumerable<PageContentAppend> appends)
    {
        foreach (var group in appends.GroupBy(a => a.PageIndex))
        {
            if (group.Key < 0 || group.Key >= doc.Pages.Count)
                throw new InvalidOperationException($"Page {group.Key + 1} is out of range for a content append.");
            var page = doc.Pages[group.Key];
            var fonts = page.Resources.Elements.GetDictionary("/Font");
            if (fonts is null) { fonts = new PdfDictionary(doc); page.Resources.Elements["/Font"] = fonts; }

            page.Contents.PrependContent().CreateStream(System.Text.Encoding.ASCII.GetBytes("q\n"));
            var sb = new System.Text.StringBuilder("Q\n");
            int n = 1;
            // 쪽의 앱 글꼴 줄이 모두 한 서브셋을 나눠 쓴다(줄마다 글꼴 객체를 만들지 않음 — 글자 집합을 합친다).
            while (fonts.Elements.ContainsKey("/SedolF" + n)) n++;
            string name = "SedolF" + n;
            fonts.Elements["/" + name] = FontEmbedder.Embed(doc, GetFont(), MergeFontUse(group.Select(a => a.Font)));
            foreach (var a in group) sb.Append("q\n").Append(a.Content.Replace("{F}", name)).Append("Q\n");
            page.Contents.AppendContent().CreateStream(System.Text.Encoding.ASCII.GetBytes(sb.ToString()));
        }
    }

    // ---- 내용 스트림 고쳐 쓰기 (M4x) ----

    /// <summary>
    /// 쪽마다: 내용을 풀어 해석 → PDFium 객체 목록과 대응 검증(어긋나면 중단) → 끄기·옮기기를 원본 바이트 위에서만 고침 → 새 객체(도형·이미지·앱 글꼴 줄)를 끝에 덧붙임.
    /// 바뀌지 않은 내용은 바이트 그대로다. 어느 단계든 실패하면 ContentRegenerationException(조용히 틀린 파일을 내지 않는다).
    /// </summary>
    private void ApplyContentRewrites(PdfDocument doc, IEnumerable<PageContentRewrite> rewrites, AnnotationAssets assets)
    {
        foreach (var rw in rewrites) RewritePage(doc, rw, assets, dryRun: false);
    }

    private static FontUse MergeFontUse(IEnumerable<FontUse> uses)
    {
        var list = uses.ToList();
        if (list.Count == 1) return list[0];
        var cps = list.SelectMany(u => u.Codepoints).Distinct().OrderBy(c => c).ToImmutableArray();
        var glyphs = list.SelectMany(u => u.Glyphs).GroupBy(g => g.Cid).Select(g => g.First()).OrderBy(g => g.Cid).ToImmutableArray();
        return new FontUse(cps, glyphs);
    }

    /// <summary>M5 C7: 파일을 바꾸지 않고(메모리에서 해 보고 버림) 쪽마다 직접 고쳐 쓰기가 되는지 본다. 글꼴·이미지는 넣지 않는다.</summary>
    public IReadOnlyDictionary<int, string> CheckContentRewrites(string pdfPath, string? password, IReadOnlyList<PageContentRewrite> pages, AnnotationAssets assets)
    {
        var bad = new Dictionary<int, string>();
        if (pages.Count == 0) return bad;
        byte[] bytes = File.ReadAllBytes(pdfPath);
        using var doc = PdfReader.Open(new MemoryStream(bytes), password, PdfDocumentOpenMode.Modify);
        foreach (var rw in pages)
        {
            try { RewritePage(doc, rw, assets, dryRun: true); }
            catch (ContentRegenerationException e) { bad[rw.PageIndex] = e.Reason ?? e.Message; }
        }
        return bad;
    }

    private void RewritePage(PdfDocument doc, PageContentRewrite rw, AnnotationAssets assets, bool dryRun)
    {
        int number = rw.PageIndex + 1;
        if (rw.PageIndex < 0 || rw.PageIndex >= doc.Pages.Count) throw new ContentRegenerationException(number, "page out of range");
        var page = doc.Pages[rw.PageIndex];
        var plan = ContentRewritePlanner.Plan(rw.Edits);
        if (!plan.Ok) throw new ContentRegenerationException(number, plan.Failure);

        var loaded = PageContent.Load(page);
        if (loaded.Data is null) throw new ContentRegenerationException(number, loaded.Failure);
        var parsed = ContentLexer.Parse(loaded.Data);
        var resolver = PageContent.XObjectResolver(page);
        var seg = ContentSegmenter.Segment(parsed, resolver);
        var map = ObjectMap.Verify(seg, rw.Reference.Select(r => new ReferenceObject((ObjectKind)r.Kind, new Box(r.L, r.B, r.R, r.T))).ToList());
        if (!map.Ok) throw new ContentRegenerationException(number, map.Reason);

        var res = ContentRewriter.Rewrite(parsed, seg, plan.ObjectEdits, null, resolver);
        if (!res.Ok) throw new ContentRegenerationException(number, res.Failure);
        if (!dryRun) HiddenTextCount += res.HiddenTextCount;
        byte[] data = res.Data!;

        if (plan.Snippets.Count > 0)
        {
            var resources = page.Resources;
            var snippets = new List<byte[]>();
            var images = new Dictionary<string, string>();
            int imgN = 1;
            // 앱 글꼴 줄은 쪽마다 서브셋 하나를 나눠 쓴다(글자 집합을 합침).
            string? appFontName = null;
            if (!dryRun && plan.Snippets.Any(x => x.Font is not null))
            {
                var fonts = resources.Elements.GetDictionary("/Font");
                if (fonts is null) { fonts = new PdfDictionary(doc); resources.Elements["/Font"] = fonts; }
                int fontN = 1;
                while (fonts.Elements.ContainsKey("/SedolF" + fontN)) fontN++;
                appFontName = "SedolF" + fontN;
                fonts.Elements["/" + appFontName] = FontEmbedder.Embed(doc, GetFont(), MergeFontUse(plan.Snippets.Where(x => x.Font is not null).Select(x => x.Font!)));
            }
            foreach (var sn in plan.Snippets)
            {
                string content = sn.Content;
                if (sn.Para is { } ps) content += ParagraphLineBytes(number, page, parsed, seg, ps);
                if (sn.Font is not null) content = content.Replace("{F}", appFontName ?? "SedolFDry");   // 시험 적용(dryRun)은 자리표시자만 해 본다
                if (sn.ImageAssetId is { } assetId)
                {
                    if (!assets.HasImage(assetId)) throw new ContentRegenerationException(number, "image asset missing");
                    if (dryRun) content = content.Replace("{I}", "SedolImDry");
                    else
                    {
                        if (!images.TryGetValue(assetId, out var imgName))
                        {
                            var xobjs = resources.Elements.GetDictionary("/XObject");
                            if (xobjs is null) { xobjs = new PdfDictionary(doc); resources.Elements["/XObject"] = xobjs; }
                            while (xobjs.Elements.ContainsKey("/SedolIm" + imgN)) imgN++;
                            imgName = "SedolIm" + imgN;
                            var (bytes, _, _, format) = assets.GetImage(assetId);
                            xobjs.Elements["/" + imgName] = ImageEmbedder.Embed(doc, bytes, format);
                            images[assetId] = imgName;
                        }
                        content = content.Replace("{I}", imgName);
                    }
                }
                snippets.Add(System.Text.Encoding.Latin1.GetBytes(content));
            }
            var appended = ContentRewriter.AppendOnly(ContentLexer.Parse(data), snippets);
            if (!appended.Ok) throw new ContentRegenerationException(number, appended.Failure);
            data = appended.Data!;
        }

        if (dryRun) return;
        page.Contents.Elements.Clear();
        page.Contents.AppendContent().CreateStream(data);
    }

    /// <summary>
    /// 문단의 원래 글꼴 줄 하나를 직접 인코딩해 쓴다(M5 C8). 글꼴은 <b>이미 쪽 자원에 있는</b> 글꼴 사전을 이름으로 가리킬 뿐 새 글꼴 객체를 만들지 않는다.
    /// 글 행렬의 기울기·크기는 원본 글 객체(내용 스트림의 Tm·CTM), 위치는 계획이 정한 기준선 원점, 색·선 굵기·투명도는 원본의 상태 연산자 그대로.
    /// 원본 글 객체를 쓸 수 없으면(글꼴 자원 없음·클립 글 등) 이 방식을 못 쓴다 → ContentRegenerationException(호출자가 PDFium 경로로 내려감).
    /// </summary>
    private static string ParagraphLineBytes(int number, PdfPage page, ParsedContent parsed, Segmentation seg, ParagraphLineSnippet ps)
    {
        var line = ps.Line;
        int idx = line.OriginalFontSource.OriginalIndex;
        if (line.Encoded is null || idx < 0 || idx >= seg.Objects.Count) throw new ContentRegenerationException(number, "paragraph line source not found");
        var src = seg.Objects[idx];
        if (src.Kind != ObjectKind.Text || src.Text is not { } snap || snap.FontOp < 0 || snap.Tr is 3 or >= 4)
            throw new ContentRegenerationException(number, "paragraph source text is not a plain visible text object");
        var tf = parsed.Ops[snap.FontOp];
        string? name = tf.Operands.FirstOrDefault(x => x.Kind == OperandKind.Name)?.Text;
        double size = tf.Operands.LastOrDefault(x => x.Kind == OperandKind.Number)?.Number ?? 0;
        var fonts = page.Resources?.Elements.GetDictionary("/Font");
        if (name is null || size <= 0 || fonts?.Elements.GetDictionary("/" + name) is null)
            throw new ContentRegenerationException(number, "paragraph source font is not in the page resources");

        var tm = snap.Tm.Then(src.Ctm);
        var placement = new Mat(tm.A, tm.B, tm.C, tm.D, line.Placement.E, line.Placement.F);
        string fill = string.Join(' ', snap.ReplayOps.Select(k => Encoding.Latin1.GetString(parsed.Data, parsed.Ops[k].Start, parsed.Ops[k].End - parsed.Ops[k].Start)));
        var style = new PdfEditor.ContentStream.Fonts.TextLineStyle((float)size, line.Tc, line.Tz, 0, snap.Tr, fill.Length > 0 ? fill : null);
        var bytes = PdfEditor.ContentStream.Fonts.TextLineWriter.Write(line.TwoByte, name, new PdfEditor.ContentStream.Fonts.EncodedText(line.Text, line.Encoded, default),
            style, placement, Math.Abs(line.JustifyExtra) > 1e-6f ? line.JustifyExtra : null);
        return Encoding.Latin1.GetString(bytes);
    }

    // ---- 고치기 ----

    private void ApplyOps(PdfDocument doc, PdfDictionary annot, int page, IEnumerable<PatchOp> ops,
        List<(PdfDictionary, string, string?, int)> pending, List<(int, PdfDictionary, EnsurePopup)> popups,
        AnnotationAssets assets, Dictionary<string, PdfReference> images)
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
                case SetReal o:
                    annot.Elements[o.Key] = new PdfReal(o.Value);
                    break;
                case AddApOpacity o:
                    AddApOpacityResource(doc, annot, o);
                    break;
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
                case AddApImage o:
                    AddApImageResource(doc, annot, o, assets, images);
                    break;
                case EmbedFile o:
                    EmbedFileAttachment(doc, annot, o, assets);
                    break;
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

    /// <summary>AP 폼의 /Resources /XObject /이름 에 이미지를 넣는다. 같은 이미지 id는 문서에 한 번만 만들고 참조를 나눠 쓴다.</summary>
    private static void AddApImageResource(PdfDocument doc, PdfDictionary annot, AddApImage op, AnnotationAssets assets, Dictionary<string, PdfReference> images)
    {
        var form = Resolve(Resolve(annot.Elements["/AP"])?.Elements["/N"])
            ?? throw new InvalidOperationException("AddApImage needs an annotation appearance stream (/AP /N).");
        if (!images.TryGetValue(op.ImageId, out var imageRef))
        {
            if (!assets.HasImage(op.ImageId)) throw new InvalidOperationException($"Image '{op.ImageId}' is not in the annotation assets.");
            var (data, _, _, format) = assets.GetImage(op.ImageId);
            imageRef = ImageEmbedder.Embed(doc, data, format);
            images[op.ImageId] = imageRef;
        }
        var resources = Resolve(form.Elements["/Resources"]);
        if (resources is null)
        {
            resources = new PdfDictionary(doc);
            form.Elements["/Resources"] = resources;
        }
        var xobjects = Resolve(resources.Elements["/XObject"]);
        if (xobjects is null)
        {
            xobjects = new PdfDictionary(doc);
            resources.Elements["/XObject"] = xobjects;
        }
        xobjects.Elements[op.ResourceName.StartsWith('/') ? op.ResourceName : "/" + op.ResourceName] = imageRef;
    }

    /// <summary>/FS &lt;&lt;/Type /Filespec /F /UF /EF &lt;&lt;/F 스트림&gt;&gt;&gt;&gt; 를 만들어 주석에 건다. 스트림은 Flate로 압축하고 /Params /Size를 적는다.</summary>
    private static void EmbedFileAttachment(PdfDocument doc, PdfDictionary annot, EmbedFile op, AnnotationAssets assets)
    {
        if (!assets.HasFile(op.AttachmentId)) throw new InvalidOperationException($"Attachment '{op.AttachmentId}' is not in the annotation assets.");
        var (_, data) = assets.GetFile(op.AttachmentId);
        if (data.Length > AnnotationAssets.MaxFileBytes)
            throw new InvalidOperationException($"Attachment is larger than {AnnotationAssets.MaxFileBytes / (1024 * 1024)} MB.");

        var stream = new PdfDictionary(doc);
        stream.Elements["/Type"] = new PdfName("/EmbeddedFile");
        var pars = new PdfDictionary(doc);
        pars.Elements["/Size"] = new PdfInteger(data.Length);
        stream.Elements["/Params"] = pars;
        stream.CreateStream(PdfSharp.Pdf.Filters.Filtering.FlateDecode.Encode(data, PdfFlateEncodeMode.BestCompression));
        stream.Elements["/Filter"] = new PdfName("/FlateDecode");
        doc.Internals.AddObject(stream);

        var ef = new PdfDictionary(doc);
        ef.Elements["/F"] = stream.Reference!;
        var fs = new PdfDictionary(doc);
        fs.Elements["/Type"] = new PdfName("/Filespec");
        string fileName = AttachmentNames.Sanitize(op.FileName);
        fs.Elements["/F"] = MakeString(fileName);
        fs.Elements["/UF"] = MakeString(fileName);
        fs.Elements["/EF"] = ef;
        doc.Internals.AddObject(fs);
        annot.Elements["/FS"] = fs.Reference!;
    }

    private static void AddApOpacityResource(PdfDocument doc, PdfDictionary annot, AddApOpacity op)
    {
        var form = Resolve(Resolve(annot.Elements["/AP"])?.Elements["/N"])
            ?? throw new InvalidOperationException("AddApOpacity needs an annotation appearance stream (/AP /N).");
        var resources = Resolve(form.Elements["/Resources"]);
        if (resources is null)
        {
            resources = new PdfDictionary(doc);
            form.Elements["/Resources"] = resources;
        }
        var states = Resolve(resources.Elements["/ExtGState"]);
        if (states is null)
        {
            states = new PdfDictionary(doc);
            resources.Elements["/ExtGState"] = states;
        }
        var gs = new PdfDictionary(doc);
        gs.Elements["/Type"] = new PdfName("/ExtGState");
        gs.Elements["/CA"] = new PdfReal(op.Opacity);
        gs.Elements["/ca"] = new PdfReal(op.Opacity);
        states.Elements["/GS"] = gs;
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

