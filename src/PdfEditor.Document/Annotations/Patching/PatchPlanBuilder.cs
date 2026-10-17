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
                if (!e.Deleted && !e.IsOriginal && e.Current.Kind == AnnotKind.FreeText)
                {
                    var m = e.Current;
                    var ap = (appearance ?? throw new InvalidOperationException("FreeText needs an IAppearanceSource to build its appearance."))
                        .BuildPatchAppearance(m, rotation);
                    creates.Add(new AnnotationCreate(pageIndex, "FreeText", m.Name ?? Guid.NewGuid().ToString("D"), m.Rect,
                        m.Flags | PdfAnnotFlags.Print, FreeTextOps(m, ap, isNew: true)));
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
                }
                else if (e.IsOriginal && e.Owned && e.Current.Kind == AnnotKind.FreeText)
                {
                    // ④ 손댄 원본 FreeText: 같은 Ops를 그 자리에서 적용한다(A13). 모르는 키는 그대로 남는다.
                    var ap = (appearance ?? throw new InvalidOperationException("FreeText needs an IAppearanceSource to build its appearance."))
                        .BuildPatchAppearance(e.Current, rotation);
                    ops.AddRange(FreeTextOps(e.Current, ap, isNew: false));
                }
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
