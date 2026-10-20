using Avalonia;
using Avalonia.Media.Imaging;
using Pdf.Native;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using RectangleF = System.Drawing.RectangleF;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 스냅숏(홈 탭, 2026-10-20 사용자 요청): 쪽의 한 영역을 화면과 같은 모양으로 그림 한 장에 그린다.
/// ① 전용 스레드에서 PDFium이 쪽 내용 + 원본 주석(손댄 원본은 숨김, 화면 타일과 같은 옵션)을 그리고
/// ② UI 스레드에서 그 위에 주석 층(AnnotationOverlay — 화면과 같은 코드)을 덧그린다. 저장하지 않은 새 주석도 들어간다.
/// </summary>
public static class SnapshotRenderer
{
    /// <summary>기본 해상도: 192dpi(화면 100%의 두 배). 긴 변은 MaxSidePx를 넘지 않게 줄인다.</summary>
    public const double DefaultPixelsPerPt = 192.0 / 72.0;
    public const int MaxSidePx = 8000;

    /// <summary>regionPt: 쪽 좌표(pt) 영역. 쪽 밖은 잘라 낸다. 쪽이 없거나 영역이 비면 null. UI 스레드에서 부른다.</summary>
    public static async Task<RenderTargetBitmap?> RenderAsync(DocumentSession s, PageId page, PdfRectF regionPt, double pixelsPerPt = DefaultPixelsPerPt)
    {
        var raster = await s.Thread.InvokeAsync(() =>
        {
            if (s.IndexOf(page) < 0) return null;
            var p = s.GetLoadedPage(page);
            var b = p.BoundsPt;
            var clip = RectangleF.Intersect(RectangleF.FromLTRB(regionPt.Left, regionPt.Bottom, regionPt.Right, regionPt.Top), b);
            if (clip.Width <= 0 || clip.Height <= 0) return null;
            double scale = pixelsPerPt;
            double longSide = Math.Max(clip.Width, clip.Height) * scale;
            if (longSide > MaxSidePx) scale *= MaxSidePx / longSide;
            var tr = new PageTransform(b, p.Rotation, scale);
            var v = tr.ToView(clip);
            int x = (int)Math.Floor(v.X), y = (int)Math.Floor(v.Y);
            int w = Math.Max(1, (int)Math.Ceiling(v.X + v.W) - x), h = Math.Max(1, (int)Math.Ceiling(v.Y + v.H) - y);
            var bytes = p.RenderTile(scale, x, y, w, h, new RenderOptions(WithAnnotations: true, HiddenAnnotIndices: s.Annotations.HiddenPdfIndices(page)));
            return new Raster(bytes, x, y, w, h, b, p.Rotation, scale);
        });
        if (raster is null) return null;

        var rtb = new RenderTargetBitmap(new PixelSize(raster.W, raster.H), new Vector(96, 96));
        using var baseImage = new BitmapHandle(raster.Bgra, raster.W, raster.H);
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawImage(baseImage.Bitmap, new Rect(0, 0, raster.W, raster.H));
            // 쪽 왼쪽 위가 (-x, -y)에 오도록 놓으면 화면 그리기와 같은 변환이 된다(96dpi에서 1 DIP = 1 픽셀).
            var tr = new PageTransform(raster.Bounds, raster.Rotation, raster.Scale);
            AnnotationOverlay.DrawPage(ctx, s.Annotations.Get(page), new Rect(-raster.X, -raster.Y, 0, 0), tr, raster.Rotation, NoPreviews);
        }
        return rtb;
    }

    private static readonly IReadOnlyDictionary<AnnotId, Annotation?> NoPreviews = new Dictionary<AnnotId, Annotation?>();

    private sealed record Raster(byte[] Bgra, int X, int Y, int W, int H, RectangleF Bounds, int Rotation, double Scale);
}
