using Pdf.Native;

namespace PdfEditor.Document.Content;

/// <summary>화면 hit test용 객체 하나(편집을 적용한 뒤의 모습). Order가 클수록 위에 그려진다.</summary>
public sealed record PageObjectEntry(ObjectRef Ref, PdfPageObjectType Type, int Order, PdfRectF Bounds, PdfQuadF Quad,
    string? TextPreview, bool IsFrame);

/// <summary>
/// 한 쪽의 보이는(켜진) 객체 목록. (PageId, Version)마다 한 번 만들어 UI가 읽는다.
/// 쪽 면적의 90 % 이상을 덮는 객체는 IsFrame(테두리·배경) — 다른 후보가 없을 때만 고른다(결정 C8).
/// </summary>
public sealed class PageObjectIndex(PageId page, int version, IReadOnlyList<PageObjectEntry> entries)
{
    public const float FrameAreaRatio = 0.9f;
    public const int TextPreviewMax = 80;

    public PageId Page { get; } = page;
    public int Version { get; } = version;
    public IReadOnlyList<PageObjectEntry> Entries { get; } = entries;

    /// <summary>점 p(쪽 pt)를 덮는 객체를 위에서부터. 테두리는 맨 뒤로. filter가 있으면 그것만.</summary>
    public IReadOnlyList<PageObjectEntry> HitTest(PdfPointF p, float tolerancePt, Func<PageObjectEntry, bool>? filter = null)
    {
        var hits = Entries.Where(e => (filter?.Invoke(e) ?? true) && e.Bounds.Inflate(tolerancePt).Contains(p))
            .OrderBy(e => e.IsFrame).ThenByDescending(e => e.Order).ToList();
        return hits;
    }

    /// <summary>PDFium 전용 스레드에서 만든다.</summary>
    internal static PageObjectIndex Build(PageId id, int version, PdfPage page, ReplayResult r, PdfTextPage? text)
    {
        var size = page.SizePt;
        float pageArea = Math.Max(1f, size.Width * size.Height);
        // 그려지는 순서: 쪽의 현재 객체 순서. 핸들 → 순서 번호
        var order = new Dictionary<IntPtr, int>();
        int n = page.ObjectCount;
        for (int i = 0; i < n; i++) order[page.GetObject(i).Handle] = i;

        var list = new List<PageObjectEntry>();
        void Add(ObjectRef rf, PdfPageObject o)
        {
            if (!o.IsActive) return;
            var b = o.Bounds;
            if (b.Width <= 0 && b.Height <= 0) return;
            string? preview = null;
            if (o.Type == PdfPageObjectType.Text && text is not null)
            {
                var t = o.GetText(text);
                if (t is not null) preview = t.Length > TextPreviewMax ? t[..TextPreviewMax] : t;
            }
            bool frame = b.Width * b.Height >= FrameAreaRatio * pageArea;
            list.Add(new PageObjectEntry(rf, o.Type, order.GetValueOrDefault(o.Handle, -1), b, o.RotatedBounds, preview, frame));
        }
        for (int i = 0; i < r.Originals.Count; i++) Add(ObjectRef.Original(i), r.Originals[i]);
        foreach (var (eid, o) in r.Inserted) Add(ObjectRef.Inserted(eid), o);
        return new PageObjectIndex(id, version, list);
    }
}
