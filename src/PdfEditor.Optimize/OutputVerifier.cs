using Pdf.Native;

namespace PdfEditor.Optimize;

/// <summary>
/// 줄인 파일 검증: 쪽 수가 같고, 표본 쪽(최대 20쪽, 고르게)을 작게 그려 원본과 평균 밝기 차이가 작아야 한다.
/// 이미지 쪽은 JPEG 때문에 픽셀이 조금씩 달라지므로 기준을 넓게 잡았다(쪽이 비거나 새까맣게 변한 것을 잡는 용도).
/// 호출 스레드는 PDFium 전용 스레드가 아니어야 한다.
/// </summary>
public static class OutputVerifier
{
    private const int MaxSamples = 20;
    private const int ThumbWidth = 64;
    private const double MaxMeanAbsDiff = 12.0;   // 0~255

    public static bool Verify(PdfiumThread t, string originalPath, string reducedPath, string? password)
        => t.InvokeAsync(() =>
        {
            using var a = Pdf.Native.PdfDocument.Open(t, originalPath, password);
            using var b = Pdf.Native.PdfDocument.Open(t, reducedPath, password);
            int n = a.PageCount;
            if (n != b.PageCount) return false;
            int samples = Math.Min(n, MaxSamples);
            for (int k = 0; k < samples; k++)
            {
                int i = samples == n ? k : (int)((long)k * (n - 1) / Math.Max(1, samples - 1));
                var pa = Thumb(a, i);
                var pb = Thumb(b, i);
                if (pa.Length != pb.Length) return false;
                long sum = 0;
                for (int j = 0; j < pa.Length; j += 4)
                    sum += Math.Abs(pa[j] - pb[j]) + Math.Abs(pa[j + 1] - pb[j + 1]) + Math.Abs(pa[j + 2] - pb[j + 2]);
                double mean = sum / (pa.Length / 4.0 * 3.0);
                if (mean > MaxMeanAbsDiff) return false;
            }
            return true;
        }).GetAwaiter().GetResult();

    private static byte[] Thumb(Pdf.Native.PdfDocument doc, int index)
    {
        using var page = doc.LoadPage(index);
        var size = page.SizePt;
        double scale = ThumbWidth / Math.Max(1.0, size.Width);
        int w = ThumbWidth, h = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        return page.RenderTile(scale, 0, 0, w, h, true);
    }
}
