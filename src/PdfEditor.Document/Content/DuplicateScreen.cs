using Pdf.Native;

namespace PdfEditor.Document.Content;

/// <summary>
/// 원본 개체의 화면 사본(0.9.0). PDFium 공개 API에는 개체를 그대로 복제하는 함수가 없어 종류별로 비슷한 객체를 다시 만든다.
/// 글자·경로는 같은 모양의 PDFium 객체, 그림은 마스크까지 적용한 비트맵, 폼·셰이딩은 그 객체만 켠 채 그린 투명 비트맵이다.
/// 저장본은 이것을 쓰지 않고 원본 연산자를 복사하므로(ContentRewritePlanner) 화면과 달라도 파일은 정확하다.
/// </summary>
internal static class DuplicateScreen
{
    /// <summary>화면 사본을 만들 수 있는 개체인가. 글자는 글이 있고 글꼴이 있을 때, 경로는 모양을 읽을 수 있을 때, 그림·폼·셰이딩은 경계 넓이가 0보다 클 때.</summary>
    public static bool CanDuplicate(PdfPageObject src)
    {
        if (src.Type is not (PdfPageObjectType.Text or PdfPageObjectType.Path or PdfPageObjectType.Image or PdfPageObjectType.Form or PdfPageObjectType.Shading)) return false;
        if (src.Type == PdfPageObjectType.Text && src.Font is not null)
        {
            using var tp = src.Page.LoadText();
            if (!string.IsNullOrEmpty(src.GetText(tp))) return true;
        }
        else if (src.Type == PdfPageObjectType.Path && src.ReadPathGeometry(int.MaxValue) is not null) return true;
        var b = src.Bounds;
        return b.Width > 0 && b.Height > 0;   // 글·경로를 같은 모양으로 못 만들어도 그림으로 보여 줄 수 있으면 복사한다(저장본은 원본 연산자를 쓴다)
    }

    /// <summary>src(원본 쪽의 편집 없는 객체)의 사본을 만든다(target 쪽에는 아직 넣지 않음). 못 만들면 null.</summary>
    public static PdfPageObject? Create(PdfPage target, PdfPage sourcePage, PdfPageObject src, PdfMatrix then)
        => CreateTyped(target, sourcePage, src, then) ?? CreateAsBitmap(target, sourcePage, src, then);

    private static PdfPageObject? CreateTyped(PdfPage target, PdfPage sourcePage, PdfPageObject src, PdfMatrix then)
    {
        switch (src.Type)
        {
            case PdfPageObjectType.Text:
            {
                using var tp = sourcePage.LoadText();
                var text = src.GetText(tp);
                if (string.IsNullOrEmpty(text) || target.CreateTextLike(src, text) is not { } t) return null;
                t.Matrix = src.Matrix.Then(then);
                return t;
            }
            case PdfPageObjectType.Path:
            {
                if (src.ReadPathGeometry(int.MaxValue) is not { } g || g.Subpaths.Count == 0 || g.Subpaths[0].Points.Length == 0) return null;
                PdfPageObject? p = null;
                foreach (var sp in g.Subpaths)
                {
                    if (sp.Points.Length == 0) continue;
                    if (p is null) p = target.CreatePath(sp.Points[0].X, sp.Points[0].Y);
                    else p.MoveTo(sp.Points[0].X, sp.Points[0].Y);
                    for (int i = 1; i < sp.Points.Length; i++) p.LineTo(sp.Points[i].X, sp.Points[i].Y);
                    if (sp.Closed) p.ClosePath();
                }
                if (p is null) return null;
                if (src.FillColor is { } fc) p.FillColor = fc;
                if (src.StrokeColor is { } sc) p.StrokeColor = sc;
                var m = src.Matrix;
                float scale = (float)Math.Sqrt(Math.Abs((double)m.A * m.D - (double)m.B * m.C));   // 선 굵기는 행렬 배율을 따른다
                p.StrokeWidth = src.StrokeWidth * (scale > 0 ? scale : 1f);
                p.SetDrawMode(fill: g.Filled, stroke: g.Stroked);
                p.TransformBy(then);
                return p;
            }
            case PdfPageObjectType.Image:
            {
                if (src.RenderImageBgra(sourcePage) is not { } r) return null;
                return target.CreateImage(r.Bgra, r.W, r.H, src.Matrix.Then(then));
            }
            case PdfPageObjectType.Form or PdfPageObjectType.Shading:
                return CreateAsBitmap(target, sourcePage, src, then);
            default:
                return null;
        }
    }

    /// <summary>원본 쪽(편집 없음)에서 이 객체만 켜고 경계를 투명 배경에 그려 그 그림을 사본으로 쓴다(폼·셰이딩, 그리고 같은 모양으로 못 만드는 글·경로).</summary>
    private static PdfPageObject? CreateAsBitmap(PdfPage target, PdfPage sourcePage, PdfPageObject src, PdfMatrix then)
    {
        var b = src.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return null;
        int n = sourcePage.ObjectCount;
        var wasActive = new bool[n];
        var objs = new PdfPageObject[n];
        for (int i = 0; i < n; i++) { objs[i] = sourcePage.GetObject(i); wasActive[i] = objs[i].IsActive; }
        (byte[] Bgra, int W, int H) bmp;
        try
        {
            for (int i = 0; i < n; i++) objs[i].IsActive = objs[i].Handle == src.Handle;
            bmp = sourcePage.RenderRegionTransparent(b, 2.0);
        }
        finally { for (int i = 0; i < n; i++) objs[i].IsActive = wasActive[i]; }
        var place = new PdfMatrix(b.Width, 0, 0, b.Height, b.Left, b.Bottom).Then(then);
        return target.CreateImage(bmp.Bgra, bmp.W, bmp.H, place);
    }
}
