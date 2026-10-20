using SkiaSharp;

namespace PdfEditor.TestFixtures;

/// <summary>시험용 실제 JPEG(SkiaSharp로 인코드). 같은 입력이면 같은 바이트.</summary>
public static class TestJpeg
{
    public static byte[] Create(int w, int h, Func<int, int, (byte R, byte G, byte B)> pixel, int quality = 90)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = pixel(x, y);
                bmp.SetPixel(x, y, new SKColor(r, g, b));
            }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }
}
