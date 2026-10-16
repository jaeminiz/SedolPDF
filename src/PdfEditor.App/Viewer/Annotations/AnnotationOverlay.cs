using Avalonia;
using Avalonia.Media;
using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Viewer.Annotations;

/// <summary>
/// 주석 층: 우리가 그리는(ModelDrawn) 주석과 만드는 중인 미리보기를 쪽 위에 그린다.
/// 손대지 않은 원본은 PDFium 타일이 이미 그렸으므로 그리지 않는다. 변환은 PageTransform.Matrix(쪽 pt → 쪽 왼쪽 위 기준 화면)만 쓴다.
/// </summary>
public static class AnnotationOverlay
{
    /// <param name="pageRect">쪽이 그려질 사각형(변환 기준 = 왼쪽 위).</param>
    /// <param name="tr">쪽 pt → 화면(DIP) 변환. scale = PageLayout.Scale(또는 썸네일 배율).</param>
    /// <param name="previews">주석 번호 → 미리보기. 값 null = 그리지 않음. 쪽에 없는 번호는 새 미리보기로 맨 위에 그린다.</param>
    public static void DrawPage(DrawingContext ctx, PageAnnotations page, Rect pageRect, PageTransform tr, int rotation,
        IReadOnlyDictionary<AnnotId, Annotation?> previews)
    {
        var m = tr.Matrix;
        var matrix = new Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX + pageRect.X, m.OffsetY + pageRect.Y);
        using (ctx.PushTransform(matrix))
        {
            HashSet<AnnotId>? onPage = previews.Count > 0 ? new() : null;
            foreach (var item in page.Items)
            {
                onPage?.Add(item.Data.Id);
                if (previews.TryGetValue(item.Data.Id, out var pv))
                {
                    if (pv is not null) Draw(ctx, pv, rotation);
                }
                else if (item.ModelDrawn) Draw(ctx, item.Data, rotation);
            }
            if (onPage is not null)
                foreach (var (id, pv) in previews)
                    if (pv is not null && !onPage.Contains(id)) Draw(ctx, pv, rotation);
        }
    }

    private static void Draw(DrawingContext ctx, Annotation a, int rotation)
    {
        foreach (var (geo, fill, stroke) in AppearanceGeometry.Get(a, rotation).Items)
            ctx.DrawGeometry(fill, stroke, geo);
    }
}
