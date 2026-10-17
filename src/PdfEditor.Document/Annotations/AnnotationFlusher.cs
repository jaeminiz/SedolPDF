using System.Security;
using System.Text;
using Pdf.Native;
using PdfEditor.Document.Saving;

namespace PdfEditor.Document.Annotations;

/// <summary>
/// 주석 모델을 PDFium 문서에 반영한다 (저장·인쇄·자동 복구 직전). PDFium 전용 스레드에서만 부른다.
/// 원본 주석은 바뀐 값만 PDFium setter로 덮어써서 모르는 키를 보존하고, 손대지 않은 원본은 건드리지 않는다.
/// </summary>
internal static class AnnotationFlusher
{
    public static void Flush(DocumentSession s, IAppearanceSource appearance)
    {
        s.Thread.AssertOnThread();
        // 문서에 없는 쪽(삭제되어 UndoStash에 있는 쪽)은 건너뛰고 다음 Flush로 미룬다.
        foreach (var (pageId, entries) in s.Annotations.PagesWithChanges().ToList())
            if (s.IndexOf(pageId) >= 0)
                FlushPage(s, pageId, entries, appearance);
    }

    private static void FlushPage(DocumentSession s, PageId pageId, IReadOnlyList<AnnotEntry> entries, IAppearanceSource appearance)
    {
        var page = s.GetLoadedPage(pageId);
        int n0 = page.AnnotationCount;
        int rotation = page.Rotation;

        var survivors = entries.Where(e => !e.Deleted).ToList();
        // 패치 가능한 문서: 삭제된 원본은 PDFium에서 지우지 않고(타일에서만 숨김) 저장 파일에서만 패치로 지운다(A4).
        // 그래야 실행 취소하면 원래 키·Popup·답글이 그대로 돌아온다. 패치를 못 하는(암호) 문서는 M2 방식.
        bool keepOriginals = SavePolicy.CanPatch(s);
        var kept = entries.Where(e => e.Deleted && e.IsOriginal && e.PdfIndex is not null && keepOriginals).ToList();
        var deleted = entries.Where(e => e.Deleted && e.PdfIndex is not null && !kept.Contains(e)).ToList();
        foreach (var k in kept)
            if (k.PdfIndex!.Value < 0 || k.PdfIndex.Value >= n0) throw new InvalidOperationException("Annotation index is out of range.");

        var survivorIdx = new HashSet<int>(survivors.Where(e => e.PdfIndex is not null).Select(e => e.PdfIndex!.Value));
        var remove = ComputeRemoval(page, n0, deleted, survivorIdx);
        foreach (int i in remove.Reverse()) page.RemoveAnnotation(i);

        int Shift(int old)
        {
            int n = 0;
            foreach (int r in remove) { if (r < old) n++; else break; }
            return old - n;
        }

        var newIndices = new Dictionary<AnnotEntry, int>();
        foreach (var k in kept) newIndices[k] = Shift(k.PdfIndex!.Value);
        int created = 0;
        foreach (var e in survivors)
        {
            if (e.PdfIndex is int old)
            {
                int idx = Shift(old);
                newIndices[e] = idx;
                if (e.Owned && e.Revision != e.FlushedRevision)
                {
                    using var a = page.GetAnnotation(idx);
                    Write(a, e.Current, isNew: false, rotation, appearance);
                }
            }
            else
            {
                using var a = page.CreateAnnotation(e.Current.Kind.ToPdfSubtype());
                Write(a, e.Current, isNew: true, rotation, appearance);
                created++;
                newIndices[e] = page.AnnotationCount - 1;
            }
        }

        if (page.AnnotationCount != n0 - remove.Count + created)
            throw new InvalidOperationException("Annotation count after flush does not match the expected count.");
        s.Annotations.CompleteFlush(pageId, newIndices);
    }

    /// <summary>
    /// 지울 번호: 삭제 항목 + 딸린 Popup + (원본이면) 그 항목을 /IRT로 가리키는 답글과 그 Popup.
    /// Flush(M2 방식)와 PatchPlanBuilder(저장 파일에서 지우기)가 같은 규칙을 쓴다. 살아 있는 항목의 번호(survivorIdx)는 지우지 않는다.
    /// </summary>
    internal static SortedSet<int> ComputeRemoval(PdfPage page, int n0, IReadOnlyList<AnnotEntry> deleted, ISet<int> survivorIdx)
    {
        var remove = new SortedSet<int>();
        foreach (var d in deleted)
        {
            int i = d.PdfIndex!.Value;
            if (i < 0 || i >= n0) throw new InvalidOperationException("Annotation index is out of range.");
            remove.Add(i);
            if (d.IsOriginal && page.GetLinkedAnnotationIndex(i, "Popup") is int pop && !survivorIdx.Contains(pop)) remove.Add(pop);
        }
        if (deleted.Any(d => d.IsOriginal))
        {
            bool grew;
            do
            {
                grew = false;
                for (int i = 0; i < n0; i++)
                {
                    if (remove.Contains(i) || survivorIdx.Contains(i)) continue;
                    if (page.GetLinkedAnnotationIndex(i, "IRT") is not int parent || !remove.Contains(parent)) continue;
                    remove.Add(i);
                    grew = true;
                    if (page.GetLinkedAnnotationIndex(i, "Popup") is int pop && !survivorIdx.Contains(pop)) remove.Add(pop);
                }
            } while (grew);
        }
        return remove;
    }

    private static void Write(PdfAnnotation a, Annotation m, bool isNew, int rotation, IAppearanceSource appearance)
    {
        // SetColor는 AP가 있으면 실패하므로 AP를 먼저 지우고, 마지막에 우리 AP를 쓴다.
        if (!isNew) a.SetAppearance(null);
        a.Rect = m.Rect;
        if (m.Color is { } c) a.SetColor(PdfColorType.Color, c.R, c.G, c.B, m.Opacity);
        if (m.Kind is AnnotKind.Square or AnnotKind.Circle && m.FillColor is { } f)
            a.SetColor(PdfColorType.Interior, f.R, f.G, f.B, m.Opacity);
        if (m.Kind is AnnotKind.Ink or AnnotKind.Square or AnnotKind.Circle) a.SetBorderWidth(m.BorderWidth);
        if (m.Kind.IsTextMarkup() && !m.Quads.IsDefaultOrEmpty) a.SetQuads(m.Quads);
        if (m.Kind == AnnotKind.Ink && !m.InkStrokes.IsDefaultOrEmpty)
            a.SetInkStrokes([.. m.InkStrokes.Select(st => (IReadOnlyList<PdfPointF>)[.. st])]);

        bool contentsChanged = false;
        if (m.Contents is not null && a.GetString("Contents") != m.Contents)
        {
            a.SetString("Contents", m.Contents);
            contentsChanged = true;
        }
        SetIfChanged(a, "T", m.Author);
        SetIfChanged(a, "Subj", m.Subject);
        if (m.ModifiedAt is { } mod) SetIfChanged(a, "M", PdfDate.Format(mod));
        if (isNew)
        {
            a.Flags = m.Flags | PdfAnnotFlags.Print;
            a.SetString("CreationDate", PdfDate.Format(m.CreatedAt ?? DateTimeOffset.Now));
        }
        if (m.Name is not null) SetIfChanged(a, "NM", m.Name);
        else if (!a.HasKey("NM")) a.SetString("NM", Guid.NewGuid().ToString("D"));
        if (contentsChanged && a.HasKey("RC")) a.SetString("RC", MinimalXhtml(m.Contents!));

        a.SetAppearance(appearance.BuildContent(m, rotation));
    }

    private static void SetIfChanged(PdfAnnotation a, string key, string? value)
    {
        if (value is null) return;
        if (a.GetString(key) != value) a.SetString(key, value);
    }

    private static string MinimalXhtml(string text)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\">");
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            sb.Append("<p>").Append(SecurityElement.Escape(line)).Append("</p>");
        return sb.Append("</body>").ToString();
    }
}
