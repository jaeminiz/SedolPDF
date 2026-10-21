using Pdf.Native;

namespace PdfEditor.Document.Content;

/// <summary>화면 hit test용 객체 하나(편집을 적용한 뒤의 모습). Order가 클수록 위에 그려진다.</summary>
public sealed record PageObjectEntry(ObjectRef Ref, PdfPageObjectType Type, int Order, PdfRectF Bounds, PdfQuadF Quad,
    string? TextPreview, bool IsFrame, PdfPathGeometry? Geometry = null);

/// <summary>
/// 한 쪽의 보이는(켜진) 객체 목록. (PageId, Version)마다 한 번 만들어 UI가 읽는다.
/// 쪽 면적의 90 % 이상을 덮는 객체는 IsFrame(테두리·배경) — 다른 후보가 없을 때만 고른다(결정 C8).
/// </summary>
public sealed class PageObjectIndex(PageId page, int version, IReadOnlyList<PageObjectEntry> entries)
{
    public const float FrameAreaRatio = 0.9f;
    public const int TextPreviewMax = 80;

    /// <summary>글 객체가 이보다 많은 쪽(큰 도면)은 미리보기 글을 만들지 않는다: 글 읽기가 객체마다 수 ms로 쪽 전체 글자 수에 비례해 5,000줄 도면에서 목록 만들기가 1.7 s였다(M4 성능 측정). 편집할 때 그 줄만 따로 읽는다.</summary>
    public const int TextPreviewObjectLimit = 400;

    /// <summary>경로 모양을 읽는 최소 크기(pt): 가로·세로 경계가 모두 이보다 커야 한다. 가는 직선·작은 부품은 경계 상자가 곧 모양이라 읽지 않는다(20만 객체 쪽 목록 만들기 시간 보호).</summary>
    public const float GeometryMinSidePt = 2f;

    /// <summary>긴 변이 이보다 짧은 작은 경로도 읽지 않는다(허용 오차 안이라 경계 상자와 차이가 작다). A1 도면 20만 객체에서 목록 만들기 시간을 지키려는 기준.</summary>
    public const float GeometryMinLongSidePt = 24f;

    /// <summary>선분이 이보다 많은 경로는 모양을 읽지 않고 경계 상자로 대신한다.</summary>
    public const int GeometryMaxSegments = 4096;

    public PageId Page { get; } = page;
    public int Version { get; } = version;
    public IReadOnlyList<PageObjectEntry> Entries { get; } = entries;

    /// <summary>
    /// 점 p(쪽 pt)를 덮는 객체를 위에서부터. 테두리는 맨 뒤로. filter가 있으면 그것만.
    /// 경로 모양을 아는 객체는 실제 선(획) 근처·칠한 안쪽만 맞는다(속이 빈 테두리 안쪽은 안 맞음). tolerancePt는 화면 픽셀 기준이라 확대하면 작아진다.
    /// </summary>
    public IReadOnlyList<PageObjectEntry> HitTest(PdfPointF p, float tolerancePt, Func<PageObjectEntry, bool>? filter = null)
    {
        var hits = Entries.Where(e => (filter?.Invoke(e) ?? true)
                && e.Bounds.Inflate(tolerancePt + (e.Geometry is { Stroked: true } g ? g.StrokeWidthPt / 2f : 0f)).Contains(p)
                && (e.Geometry is null || e.Geometry.Hit(p, tolerancePt)))
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

        bool previews = text is not null && r.Originals.Count(o => o.Type == PdfPageObjectType.Text) + r.Inserted.Count <= TextPreviewObjectLimit;
        var list = new List<PageObjectEntry>();
        void Add(ObjectRef rf, PdfPageObject o)
        {
            if (!o.IsActive) return;
            var b = o.Bounds;
            if (b.Width <= 0 && b.Height <= 0) return;
            string? preview = null;
            if (previews && o.Type == PdfPageObjectType.Text)
            {
                var t = o.GetText(text!);
                if (t is not null) preview = t.Length > TextPreviewMax ? t[..TextPreviewMax] : t;
            }
            bool frame = b.Width * b.Height >= FrameAreaRatio * pageArea;
            PdfPathGeometry? geom = o.Type == PdfPageObjectType.Path && b.Width > GeometryMinSidePt && b.Height > GeometryMinSidePt && Math.Max(b.Width, b.Height) >= GeometryMinLongSidePt
                ? o.ReadPathGeometry(GeometryMaxSegments) : null;
            list.Add(new PageObjectEntry(rf, o.Type, order.GetValueOrDefault(o.Handle, -1), b, o.RotatedBounds, preview, frame, geom));
        }
        for (int i = 0; i < r.Originals.Count; i++) Add(ObjectRef.Original(i), r.Originals[i]);
        foreach (var (eid, o) in r.Inserted) Add(ObjectRef.Inserted(eid), o);
        return new PageObjectIndex(id, version, list);
    }
}
