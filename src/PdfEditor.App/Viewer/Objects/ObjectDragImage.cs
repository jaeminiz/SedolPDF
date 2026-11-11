using Pdf.Native;

namespace PdfEditor.App.Viewer.Objects;

/// <summary>
/// 객체를 끄는 동안 보여 줄 그림(0.10.0, 설계서 7절). 고른 객체만 켜고 나머지는 끈 채 그 경계를 투명 배경에 그린다 —
/// <c>DuplicateScreen</c>의 켜고 끄기 방법과 같다. PDFium 전용 스레드에서 부른다. 켜짐 상태는 어떤 경우에도 되돌린다.
/// </summary>
public static class ObjectDragImage
{
    /// <summary>그림의 긴 변 최대 픽셀. 도면 쪽 전체를 고른 큰 선택에서 메모리·시간이 터지지 않게 배율을 줄인다.</summary>
    public const int MaxSidePx = 2048;

    /// <summary>끄는 동안 그림을 그릴 불투명도.</summary>
    public const double Opacity = 0.6;

    /// <summary>선택이 이만큼 많으면 그림을 만들지 않는다(점선 상자만). 객체마다 켜짐 상태를 바꾸는 비용을 제한한다.</summary>
    public const int MaxObjects = 20000;

    public static (byte[] Bgra, int W, int H)? Render(PdfPage page, IReadOnlyList<PdfPageObject> chosen, PdfRectF bounds, double scale,
        Func<PdfPage, PdfRectF, double, (byte[] Bgra, int W, int H)>? renderer = null)
    {
        if (chosen.Count == 0 || chosen.Count > MaxObjects || bounds.Width <= 0 || bounds.Height <= 0 || scale <= 0) return null;
        double longest = Math.Max(bounds.Width, bounds.Height) * scale;
        if (longest > MaxSidePx) scale = MaxSidePx / Math.Max(bounds.Width, bounds.Height) * (1 - 1e-9);
        var ids = chosen.Select(c => c.NativeId).ToHashSet();
        int n = page.ObjectCount;
        var objs = new PdfPageObject[n];
        var was = new bool[n];
        for (int i = 0; i < n; i++) { objs[i] = page.GetObject(i); was[i] = objs[i].IsActive; }
        try
        {
            for (int i = 0; i < n; i++) objs[i].IsActive = ids.Contains(objs[i].NativeId);
            return (renderer ?? ((p, b, s) => p.RenderRegionTransparent(b, s)))(page, bounds, scale);
        }
        finally { for (int i = 0; i < n; i++) objs[i].IsActive = was[i]; }
    }
}
