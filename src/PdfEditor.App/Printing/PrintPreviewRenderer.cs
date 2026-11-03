using Pdf.Native;

namespace PdfEditor.App.Printing;

/// <summary>미리보기 한 장(BGRA, 불투명). 용지 전체이며 인쇄 가능 영역 밖은 흰색이다.</summary>
public sealed record PreviewBitmap(int Width, int Height, byte[] Bgra);

/// <summary>
/// 용지 한 장의 모습을 인쇄와 같은 배치 규칙(PrintLayout)으로 그린다. PDFium 전용 스레드에서 부른다.
/// 장치 픽셀(프린터 해상도) 좌표를 outWidth/PhysicalWidth 배로 줄여 그린다.
/// </summary>
public static class PrintPreviewRenderer
{
    public static PreviewBitmap RenderSheet(PdfDocument doc, PrintSheet sheet, PrintOptions o, PrintSurface surf, int outWidth, CancellationToken ct = default)
    {
        double f = (double)outWidth / Math.Max(1, surf.PhysicalWidth);
        int bw = Math.Max(1, outWidth), bh = Math.Max(1, (int)Math.Round(surf.PhysicalHeight * f));
        var buf = new byte[checked(bw * bh * 4)];
        Array.Fill(buf, (byte)0xFF);

        int n = sheet.Cells.Length;
        for (int c = 0; c < n; c++)
        {
            ct.ThrowIfCancellationRequested();
            if (sheet.Cells[c] is not int index) continue;
            var cell = PrintLayout.Cell(c, n, surf.Width > surf.Height, surf.Width, surf.Height);
            using var page = doc.LoadPage(index);
            var size = page.SizePt;
            var p = PrintLayout.Compute(size.Width, size.Height, cell.W, cell.H, surf.DpiX, surf.DpiY, o with { PagesPerSheet = n });

            // 회전하지 않은 모습의 크기(장치 픽셀) → 미리보기 픽셀
            int devW = p.Rotate == 1 ? p.Height : p.Width, devH = p.Rotate == 1 ? p.Width : p.Height;
            double scale = devW * f / Math.Max(1f, size.Width);
            int tw = Math.Max(1, (int)Math.Round(size.Width * scale)), th = Math.Max(1, (int)Math.Round(size.Height * scale));
            if ((long)tw * th > 40_000_000) continue;   // 배율 400%처럼 용지보다 훨씬 커지면 보이는 부분만 의미가 있으므로 그리지 않고 건너뜀
            var px = page.RenderTile(scale, 0, 0, tw, th, o.WithAnnotations);
            int pw = tw, ph = th;
            if (p.Rotate == 1) { px = RotateCw(px, tw, th); (pw, ph) = (th, tw); }
            if (o.Grayscale) ToGray(px);

            int dstX = (int)Math.Round((surf.OffsetX + cell.X + p.X) * f), dstY = (int)Math.Round((surf.OffsetY + cell.Y + p.Y) * f);
            Blit(buf, bw, bh, px, pw, ph, dstX, dstY);
        }
        return new PreviewBitmap(bw, bh, buf);
    }

    /// <summary>BGRA 비트맵을 시계 방향 90도 돌린다(FPDF_RenderPage rotate=1과 같은 방향).</summary>
    public static byte[] RotateCw(byte[] src, int w, int h)
    {
        var dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 4, d = (x * h + (h - 1 - y)) * 4;
                dst[d] = src[s]; dst[d + 1] = src[s + 1]; dst[d + 2] = src[s + 2]; dst[d + 3] = src[s + 3];
            }
        return dst;
    }

    public static void ToGray(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte g = (byte)((bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000);
            bgra[i] = bgra[i + 1] = bgra[i + 2] = g;
        }
    }

    private static void Blit(byte[] dst, int dw, int dh, byte[] src, int sw, int sh, int x0, int y0)
    {
        for (int y = 0; y < sh; y++)
        {
            int dy = y0 + y;
            if (dy < 0 || dy >= dh) continue;
            int sx0 = Math.Max(0, -x0), sx1 = Math.Min(sw, dw - x0);
            if (sx1 <= sx0) continue;
            Buffer.BlockCopy(src, (y * sw + sx0) * 4, dst, (dy * dw + x0 + sx0) * 4, (sx1 - sx0) * 4);
        }
    }
}
