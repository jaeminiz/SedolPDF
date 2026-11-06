using Pdf.Native;

namespace PdfEditor.Optimize;

/// <summary>
/// 줄인 파일 검증: 쪽 수가 같고, 표본 쪽(최대 20쪽, 고르게)을 그려 원본과 "블록 평균 밝기"가 비슷해야 한다(쪽이 비었거나 새까맣게 변했거나 한 영역이 사라진 것을 잡는다).
/// 쪽을 16×16 화소 블록으로 나눠 블록마다 평균 밝기를 비교한다 — 화소 단위로 비교하면 가는 선이 촘촘한 도면은 안티앨리어싱만 달라도 크게 달라 보인다(실제 도면에서 확인).
/// atDpi: 이미지로 변환한 결과(래스터화)는 변환에 쓴 dpi와 같은 배율로 비교한다. 벡터는 작은 배율에서도 가는 선을 최소 1화소로 그려 더 진하게 나오므로,
/// 벡터 원본과 래스터를 다른 배율로 비교하면 치우친다. atDpi가 없으면(이미지 재압축: 벡터는 그대로) 작은 그림(256화소 폭)으로 비교한다.
/// 호출 스레드는 PDFium 전용 스레드가 아니어야 한다.
/// </summary>
public static class OutputVerifier
{
    private const int MaxSamples = 20;
    private const int ThumbWidth = 256;
    private const int Block = 16;
    private const double MaxMeanBlockDiff = 10.0;   // 0~255, 모든 블록의 평균 차이
    private const double MaxSingleBlockDiff = 90.0; // 0~255, 한 블록의 최대 차이

    public static bool Verify(PdfiumThread t, string originalPath, string reducedPath, string? password, int? atDpi = null, bool binarize = false, int binarizeThreshold = 170)
        => Check(t, originalPath, reducedPath, password, atDpi, binarize, binarizeThreshold).Ok;

    /// <summary>Verify와 같은 검사. 가장 나쁜 쪽의 블록 평균 차이(WorstMean)와 한 블록 최대 차이(WorstMax)도 돌려준다(시험·진단용).</summary>
    internal static (bool Ok, double WorstMean, double WorstMax) Check(PdfiumThread t, string originalPath, string reducedPath, string? password, int? atDpi = null, bool binarize = false, int binarizeThreshold = 170)
        => t.InvokeAsync(() =>
        {
            using var a = Pdf.Native.PdfDocument.Open(t, originalPath, password);
            using var b = Pdf.Native.PdfDocument.Open(t, reducedPath, password);
            int n = a.PageCount;
            if (n != b.PageCount) return (false, double.NaN, double.NaN);
            int samples = Math.Min(n, MaxSamples);
            double worstMean = 0, worstMax = 0;
            for (int k = 0; k < samples; k++)
            {
                int i = samples == n ? k : (int)((long)k * (n - 1) / Math.Max(1, samples - 1));
                var ba = BlockMeans(a, i, atDpi, binarize, binarizeThreshold);
                var bb = BlockMeans(b, i, atDpi, binarize, binarizeThreshold);
                if (ba.Length != bb.Length) return (false, double.NaN, double.NaN);
                double sum = 0, max = 0;
                for (int j = 0; j < ba.Length; j++)
                {
                    double d = Math.Abs(ba[j] - bb[j]);
                    sum += d;
                    if (d > max) max = d;
                }
                worstMean = Math.Max(worstMean, sum / ba.Length);
                worstMax = Math.Max(worstMax, max);
            }
            return (worstMean <= MaxMeanBlockDiff && worstMax <= MaxSingleBlockDiff, worstMean, worstMax);
        }).GetAwaiter().GetResult();

    /// <summary>쪽을 그려(atDpi가 있으면 그 배율, 없으면 ThumbWidth 폭) 16×16 블록마다 (R+G+B)/3 평균을 낸다.</summary>
    private static double[] BlockMeans(Pdf.Native.PdfDocument doc, int index, int? atDpi, bool binarize, int threshold)
    {
        using var page = doc.LoadPage(index);
        var size = page.SizePt;
        double scale;
        int w, h;
        if (atDpi is { } dpi)
            (scale, w, h) = Rasterizer.PixelSize(size.Width, size.Height, dpi);
        else
        {
            scale = ThumbWidth / Math.Max(1.0, size.Width);
            w = ThumbWidth;
            h = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        }
        var px = page.RenderTile(scale, 0, 0, w, h, true);
        int bw = (w + Block - 1) / Block, bh = (h + Block - 1) / Block;
        var means = new double[bw * bh];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                long sum = 0; int cnt = 0;
                for (int y = by * Block; y < Math.Min(h, (by + 1) * Block); y++)
                    for (int x = bx * Block; x < Math.Min(w, (bx + 1) * Block); x++)
                    {
                        int o = (y * w + x) * 4;
                        sum += binarize ? (px[o] < threshold || px[o + 1] < threshold || px[o + 2] < threshold ? 0 : 765) : px[o] + px[o + 1] + px[o + 2];
                        cnt++;
                    }
                means[by * bw + bx] = sum / (cnt * 3.0);
            }
        return means;
    }
}
