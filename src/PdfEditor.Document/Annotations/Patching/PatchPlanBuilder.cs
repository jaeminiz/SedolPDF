using System.Collections.Immutable;
using System.Globalization;
using Pdf.Native;
using PdfEditor.Document.Saving;

namespace PdfEditor.Document.Annotations.Patching;

/// <summary>
/// 모델 전체에서 저장 파일에 적용할 패치 계획을 새로 만든다(상태 없음, A1). Flush 직후, PDFium 전용 스레드에서 부른다.
/// 쪽 번호 = 현재 쪽 인덱스, 주석 번호 = Flush 뒤 PdfIndex(= SaveCopy 파일의 /Annots 번호).
/// 패치 길 주석(FreeText)은 appearance가 AP·글꼴 정보를 만든다. 새 주석은 PDFium 문서에 없으므로 Creates로 임시 파일 끝에 붙는다.
/// </summary>
public static class PatchPlanBuilder
{
    public static PatchPlan Build(DocumentSession s, IAppearanceSource? appearance = null)
    {
        s.Thread.AssertOnThread();
        if (!SavePolicy.CanPatch(s)) return PatchPlan.Empty;

        var patches = ImmutableArray.CreateBuilder<AnnotationPatch>();
        var creates = ImmutableArray.CreateBuilder<AnnotationCreate>();
        var removals = ImmutableArray.CreateBuilder<AnnotationRemoval>();

        foreach (var (pageId, entries) in s.Annotations.AllPages())
        {
            int pageIndex = s.IndexOf(pageId);
            if (pageIndex < 0) continue;   // 문서에 없는(삭제되어 UndoStash에 있는) 쪽
            var page = s.GetLoadedPage(pageId);
            int n0 = page.AnnotationCount;
            int rotation = page.Rotation;

            foreach (var e in entries)
            {
                // 새 패치 길 주석: PDFium 문서에 없으므로(PdfIndex 없음) 매번 새로 만든다.
                if (!e.Deleted && !e.IsOriginal && e.Current.Kind.IsPatchKind())
                {
                    var m = e.Current;
                    var ap = (appearance ?? throw new InvalidOperationException($"{m.Kind} needs an IAppearanceSource to build its appearance."))
                        .BuildPatchAppearance(m, rotation);
                    creates.Add(new AnnotationCreate(pageIndex, m.Kind.ToString(), StableName(m), m.Rect,
                        m.Flags | PdfAnnotFlags.Print, PatchKindOps(m, ap, isNew: true, ReplyParentNm(m, entries, page, n0))));
                    continue;
                }

                if (e.Deleted || e.PdfIndex is not int idx || idx < 0 || idx >= n0) continue;
                var ops = ImmutableArray.CreateBuilder<PatchOp>();

                if (!e.IsOriginal && e.Current.Kind == AnnotKind.Text)
                {
                    // ① 새 메모: Popup이 아직 없을 때만(CountDelta가 EnsurePopup마다 +1로 센다).
                    if (page.GetLinkedAnnotationIndex(idx, "Popup") is null)
                    {
                        var r = e.Current.Rect;
                        ops.Add(new EnsurePopup(new PdfRectF(r.Right + 4, r.Top - 120, r.Right + 4 + 180, r.Top), Open: false));
                    }
                }
                else if (e.IsOriginal && e.Owned && e.Current.Kind is AnnotKind.Ink or AnnotKind.Square or AnnotKind.Circle)
                {
                    // ② 손댄 원본: 두께는 /Border에 썼으므로 낡은 /BS를 지우고, 채우기를 없앴으면 /IC도 지운다.
                    ops.Add(new RemoveKeys(["/BS"]));
                    if (e.Current.Kind is AnnotKind.Square or AnnotKind.Circle && e.Current.FillColor is null)
                        ops.Add(new RemoveKeys(["/IC"]));
                    ops.AddRange(SquareCloudOps(e.Current, original: true));
                }
                else if (e.IsOriginal && e.Owned && e.Current.Kind.IsPatchKind())
                {
                    // ④ 손댄 원본(패치 길): 같은 Ops를 그 자리에서 적용한다(A13). 모르는 키는 그대로 남는다.
                    var ap = (appearance ?? throw new InvalidOperationException($"{e.Current.Kind} needs an IAppearanceSource to build its appearance."))
                        .BuildPatchAppearance(e.Current, rotation);
                    ops.AddRange(PatchKindOps(e.Current, ap, isNew: false));
                }
                if (!e.IsOriginal && e.Current.Kind == AnnotKind.Square)
                    ops.AddRange(SquareCloudOps(e.Current, original: false));   // 새 클라우드 사각형: PDFium 길 + /BE·/RD 패치
                if (ops.Count == 0) continue;

                using var a = page.GetAnnotation(idx);
                string nm = a.GetString("NM") ?? "";
                // /NM이 없는 주석은 빈 문자열과 비교되어 보호가 약하므로 하위 유형도 함께 확인한다.
                patches.Add(new AnnotationPatch(pageIndex, idx, nm, ops.ToImmutable(), e.Current.Kind.ToString()));
            }

            // ③ 삭제된 원본(A4): PDFium에는 그대로 있고 저장 파일에서만 지운다. 번호는 Flush와 같은 규칙으로 계산.
            var kept = entries.Where(e => e.Deleted && e.IsOriginal && e.PdfIndex is not null).ToList();
            if (kept.Count == 0) continue;
            var survivorIdx = new HashSet<int>(entries.Where(e => !e.Deleted && e.PdfIndex is not null).Select(e => e.PdfIndex!.Value));
            var remove = AnnotationFlusher.ComputeRemoval(page, n0, kept, survivorIdx);
            removals.Add(new AnnotationRemoval(pageIndex, [.. remove.Reverse()]));
        }

        if (patches.Count == 0 && creates.Count == 0 && removals.Count == 0) return PatchPlan.Empty;
        return new PatchPlan(patches.ToImmutable(), creates.ToImmutable(), removals.ToImmutable());
    }

    internal static ImmutableArray<PatchOp> PatchKindOps(Annotation m, PatchAppearance ap, bool isNew, string? replyParentNm = null)
        => m.Kind switch
        {
            AnnotKind.FreeText => FreeTextOps(m, ap, isNew),
            AnnotKind.Stamp => StampOps(m, ap, isNew),
            AnnotKind.FileAttachment => AttachmentOps(m, ap, isNew),
            _ => ShapeOps(m, ap, isNew, replyParentNm),
        };

    // 이 실행(프로세스)에서만 쓰는 접두어. 저장할 때마다 같은 /NM을 만들되, 다른 실행·다른 파일의 /NM과는 겹치지 않는다.
    private static readonly string SessionToken = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 새 패치 길 주석의 /NM: 모델에 이름이 있으면 그것, 없으면 (실행 토큰 + 주석 Id)로 정해 저장 때마다 같다
    /// (Caret이 StrikeOut을 /IRT로 가리킬 때와 반복 저장 때 /NM이 흔들리지 않게).
    /// </summary>
    internal static string StableName(Annotation m) => m.Name ?? $"{SessionToken}-{m.Id.Value}";

    /// <summary>
    /// 새 Caret이 묶음(Group)이면 부모 StrikeOut의 /NM. Flush가 끝난 뒤이므로 PDFium 문서의 주석에서 읽는다(Flush가 /NM을 보장).
    /// 부모가 살아 있지 않거나 /NM을 알 수 없으면 null — 묶지 않고 Caret만 저장한다.
    /// </summary>
    private static string? ReplyParentNm(Annotation m, IReadOnlyList<AnnotEntry> entries, Pdf.Native.PdfPage page, int n0)
    {
        if (m.Kind != AnnotKind.Caret || m.InReplyTo is not { } pid || m.ReplyKind != ReplyType.Group) return null;
        var parent = entries.FirstOrDefault(x => !x.Deleted && x.Current.Id == pid);
        if (parent is null) return null;
        if (parent.PdfIndex is int i && i >= 0 && i < n0)
        {
            using var a = page.GetAnnotation(i);
            if (a.GetString("NM") is { Length: > 0 } nm) return nm;
        }
        return parent.Current.Kind.IsPatchKind() && !parent.IsOriginal ? StableName(parent.Current) : parent.Current.Name;
    }

    /// <summary>PDF /Name: 기본 스탬프는 그 이름, 글자·그림 스탬프는 우리 이름(표준 이름을 쓰지 않는다).</summary>
    internal const string CustomStampName = "SedolCustom";

    /// <summary>스탬프: AP(둥근 테두리+글자 또는 그림), /Name, 글꼴·그림 자원. 원본 스탬프는 읽기 전용이라 새 스탬프만 온다.</summary>
    internal static ImmutableArray<PatchOp> StampOps(Annotation m, PatchAppearance ap, bool isNew)
    {
        var spec = m.Stamp ?? throw new InvalidOperationException("A Stamp annotation needs a StampSpec.");
        var ops = ImmutableArray.CreateBuilder<PatchOp>();
        ops.Add(new SetAppearanceStream(ap.Content, ap.BBox));
        AddOpacity(ops, m);
        if (!isNew) ops.Add(new SetNumbers("/Rect", [m.Rect.Left, m.Rect.Bottom, m.Rect.Right, m.Rect.Top]));
        ops.Add(new SetName("/Name", spec.Kind == StampKind.Builtin && spec.BuiltinName.Length > 0 ? spec.BuiltinName : CustomStampName));
        AddCommonKeys(ops, m, isNew);
        if (spec.Kind == StampKind.Image)
            ops.Add(new AddApImage("Im1", spec.ImageId ?? throw new InvalidOperationException("An image stamp needs an ImageId.")));
        else if (!ap.Font.Glyphs.IsDefaultOrEmpty) ops.Add(new AddApFont("F1", ap.Font));
        return ops.ToImmutable();
    }

    /// <summary>파일 첨부: AP(클립 아이콘), /Name /Paperclip, /FS(EmbedFile).</summary>
    internal static ImmutableArray<PatchOp> AttachmentOps(Annotation m, PatchAppearance ap, bool isNew)
    {
        var ops = ImmutableArray.CreateBuilder<PatchOp>();
        ops.Add(new SetAppearanceStream(ap.Content, ap.BBox));
        AddOpacity(ops, m);
        if (!isNew) ops.Add(new SetNumbers("/Rect", [m.Rect.Left, m.Rect.Bottom, m.Rect.Right, m.Rect.Top]));
        ops.Add(new SetName("/Name", "Paperclip"));
        if (m.Color is { } c) ops.Add(new SetNumbers("/C", [c.R / 255f, c.G / 255f, c.B / 255f]));
        else ops.Add(new RemoveKeys(["/C"]));
        AddCommonKeys(ops, m, isNew);
        ops.Add(new EmbedFile(m.AttachmentName ?? "attachment", m.AttachmentId ?? throw new InvalidOperationException("A file attachment needs an AttachmentId.")));
        return ops.ToImmutable();
    }

    private static void AddOpacity(ImmutableArray<PatchOp>.Builder ops, Annotation m)
    {
        if (m.Opacity < 1f)
        {
            ops.Add(new AddApOpacity(m.Opacity));
            ops.Add(new SetReal("/CA", m.Opacity));
        }
        else ops.Add(new RemoveKeys(["/CA"]));
    }

    private static void AddCommonKeys(ImmutableArray<PatchOp>.Builder ops, Annotation m, bool isNew)
    {
        if (m.Contents is not null)
        {
            ops.Add(new SetString("/Contents", m.Contents));
            ops.Add(new SetString("/RC", AnnotationFlusher.MinimalXhtml(m.Contents)));
        }
        if (m.Author is not null) ops.Add(new SetString("/T", m.Author));
        if (m.Subject is not null) ops.Add(new SetString("/Subj", m.Subject));
        if (m.ModifiedAt is { } mod) ops.Add(new SetString("/M", PdfDate.Format(mod)));
        if (isNew) ops.Add(new SetString("/CreationDate", PdfDate.Format(m.CreatedAt ?? m.ModifiedAt ?? DateTimeOffset.Now)));
    }

    /// <summary>Square 클라우드: /BE &lt;&lt;/S /C /I n&gt;&gt; + /RD(Painter와 같은 안쪽 간격). 클라우드가 아니면 손댄 원본의 낡은 /BE·/RD만 지운다.</summary>
    internal static ImmutableArray<PatchOp> SquareCloudOps(Annotation m, bool original)
    {
        if (m.Kind != AnnotKind.Square) return [];
        if (m.CloudIntensity > 0)
        {
            float rd = AnnotationGeometry.CloudRectInset(m.Rect, m.CloudIntensity, m.BorderWidth);
            return [new SetBorderEffect(m.CloudIntensity), new SetNumbers("/RD", [rd, rd, rd, rd])];
        }
        return original ? [new RemoveKeys(["/BE", "/RD"])] : [];
    }

    /// <summary>
    /// 선·폴리라인·다각형·Caret의 키 목록(A3). 새 주석과 손댄 원본이 같은 목록을 쓴다(원본은 Rect·CreationDate 차이만).
    /// 선 끝 /LE는 선·폴리라인에서 이름 두 개의 배열(시작, 끝)이다(FreeText의 /LE는 이름 하나).
    /// </summary>
    internal static ImmutableArray<PatchOp> ShapeOps(Annotation m, PatchAppearance ap, bool isNew, string? replyParentNm = null)
    {
        var ops = ImmutableArray.CreateBuilder<PatchOp>();
        ops.Add(new SetAppearanceStream(ap.Content, ap.BBox));
        if (m.Opacity < 1f)
        {
            ops.Add(new AddApOpacity(m.Opacity));
            ops.Add(new SetReal("/CA", m.Opacity));
        }
        else ops.Add(new RemoveKeys(["/CA"]));
        if (!isNew) ops.Add(new SetNumbers("/Rect", [m.Rect.Left, m.Rect.Bottom, m.Rect.Right, m.Rect.Top]));

        if (m.Color is { } c) ops.Add(new SetNumbers("/C", [c.R / 255f, c.G / 255f, c.B / 255f]));
        else ops.Add(new RemoveKeys(["/C"]));

        switch (m.Kind)
        {
            case AnnotKind.Line:
                if (m.Vertices.Length >= 2)
                    ops.Add(new SetNumbers("/L", [m.Vertices[0].X, m.Vertices[0].Y, m.Vertices[1].X, m.Vertices[1].Y]));
                ops.Add(new SetNames("/LE", [m.StartEnding.ToPdfName(), m.EndEnding.ToPdfName()]));
                break;
            case AnnotKind.PolyLine:
                ops.Add(new SetNumbers("/Vertices", [.. m.Vertices.SelectMany(v => new[] { v.X, v.Y })]));
                ops.Add(new SetNames("/LE", [m.StartEnding.ToPdfName(), m.EndEnding.ToPdfName()]));
                break;
            case AnnotKind.Polygon:
                ops.Add(new SetNumbers("/Vertices", [.. m.Vertices.SelectMany(v => new[] { v.X, v.Y })]));
                if (m.CloudIntensity > 0) ops.Add(new SetBorderEffect(m.CloudIntensity));
                else ops.Add(new RemoveKeys(["/BE"]));
                break;
            case AnnotKind.Caret:
                // 새 Caret만 /Sy·/RD를 정한다. 손댄 원본의 /Sy(/P 등)는 그대로 둔다.
                if (isNew)
                {
                    ops.Add(new SetName("/Sy", "/None"));
                    ops.Add(new SetNumbers("/RD", [0f, 0f, 0f, 0f]));
                }
                // 텍스트 바꾸기 묶음: 새 Caret이 /IRT(부모 StrikeOut) + /RT /Group을 건다. 원본 Caret은 이미 갖고 있으므로 건드리지 않는다.
                if (isNew && replyParentNm is not null) ops.Add(new SetInReplyTo(replyParentNm, "/Group"));
                break;
        }

        if (m.Kind != AnnotKind.Caret)
        {
            // 두께는 /Border에 쓰므로 낡은 /BS는 지운다(M2 한계 1과 같은 이유).
            ops.Add(new RemoveKeys(["/BS"]));
            ops.Add(new SetNumbers("/Border", [0f, 0f, m.BorderWidth]));
            // /IC: 선·폴리라인은 끝 모양의 채움색, 다각형은 안쪽 채움색.
            if (m.FillColor is { } f) ops.Add(new SetNumbers("/IC", [f.R / 255f, f.G / 255f, f.B / 255f]));
            else ops.Add(new RemoveKeys(["/IC"]));
        }

        if (m.Contents is not null)
        {
            ops.Add(new SetString("/Contents", m.Contents));
            ops.Add(new SetString("/RC", AnnotationFlusher.MinimalXhtml(m.Contents)));
        }
        if (m.Author is not null) ops.Add(new SetString("/T", m.Author));
        if (m.Subject is not null) ops.Add(new SetString("/Subj", m.Subject));
        if (m.ModifiedAt is { } mod) ops.Add(new SetString("/M", PdfDate.Format(mod)));
        if (isNew) ops.Add(new SetString("/CreationDate", PdfDate.Format(m.CreatedAt ?? m.ModifiedAt ?? DateTimeOffset.Now)));
        return ops.ToImmutable();
    }

    /// <summary>
    /// FreeText(텍스트 상자·타자기·설명선)의 키 목록. 새 주석과 손댄 원본이 같은 목록을 쓴다(원본은 Rect·CreationDate 차이만).
    /// /LE는 ISO 32000의 FreeText 정의대로 이름 하나(지시선 시작점 = 화살표 끝)로 쓴다. 읽을 때는 이름·배열 둘 다 받는다.
    /// </summary>
    internal static ImmutableArray<PatchOp> FreeTextOps(Annotation m, PatchAppearance ap, bool isNew)
    {
        var ops = ImmutableArray.CreateBuilder<PatchOp>();
        ops.Add(new SetAppearanceStream(ap.Content, ap.BBox));
        if (!isNew) ops.Add(new SetNumbers("/Rect", [m.Rect.Left, m.Rect.Bottom, m.Rect.Right, m.Rect.Top]));

        string contents = m.Contents ?? "";
        ops.Add(new SetString("/Contents", contents));
        var tc = m.TextColor ?? new PdfColor(0, 0, 0);
        ops.Add(new SetString("/DA", $"{N(tc.R / 255f)} {N(tc.G / 255f)} {N(tc.B / 255f)} rg /Helv {N(m.FontSizePt)} Tf"));
        ops.Add(new SetInteger("/Q", (int)m.Align));

        var intent = m.Intent ?? FreeTextIntent.TextBox;
        if (intent == FreeTextIntent.TextBox) ops.Add(new RemoveKeys(["/IT"]));
        else ops.Add(new SetName("/IT", intent == FreeTextIntent.Typewriter ? "/FreeTextTypeWriter" : "/FreeTextCallout"));

        if (intent == FreeTextIntent.Callout && !m.Vertices.IsDefaultOrEmpty)
        {
            ops.Add(new SetNumbers("/CL", [.. m.Vertices.SelectMany(v => new[] { v.X, v.Y })]));
            ops.Add(new SetName("/LE", m.EndEnding.ToPdfName()));
            var box = m.TextBox ?? m.Rect;
            ops.Add(new SetNumbers("/RD", [box.Left - m.Rect.Left, box.Bottom - m.Rect.Bottom, m.Rect.Right - box.Right, m.Rect.Top - box.Top]));
        }
        else
        {
            ops.Add(new RemoveKeys(["/CL", "/LE"]));
            ops.Add(new SetNumbers("/RD", [0f, 0f, 0f, 0f]));
        }

        // 두께는 /Border에 쓰므로 낡은 /BS는 지운다(M2 한계 1과 같은 이유).
        ops.Add(new RemoveKeys(["/BS"]));
        ops.Add(new SetNumbers("/Border", [0f, 0f, m.BorderWidth]));
        if (m.Color is { } c) ops.Add(new SetNumbers("/C", [c.R / 255f, c.G / 255f, c.B / 255f]));
        else ops.Add(new RemoveKeys(["/C"]));
        if (m.FillColor is { } f) ops.Add(new SetNumbers("/IC", [f.R / 255f, f.G / 255f, f.B / 255f]));
        else ops.Add(new RemoveKeys(["/IC"]));

        ops.Add(new SetString("/RC", AnnotationFlusher.MinimalXhtml(contents)));
        if (m.Author is not null) ops.Add(new SetString("/T", m.Author));
        if (m.Subject is not null) ops.Add(new SetString("/Subj", m.Subject));
        if (m.ModifiedAt is { } mod) ops.Add(new SetString("/M", PdfDate.Format(mod)));
        if (isNew) ops.Add(new SetString("/CreationDate", PdfDate.Format(m.CreatedAt ?? m.ModifiedAt ?? DateTimeOffset.Now)));

        if (!ap.Font.Glyphs.IsDefaultOrEmpty) ops.Add(new AddApFont("F1", ap.Font));
        return ops.ToImmutable();
    }

    private static string N(float v)
    {
        var s = v.ToString("0.###", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
