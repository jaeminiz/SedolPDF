using System.IO.Compression;

namespace PdfEditor.TestFixtures;

/// <summary>테스트용 이미지 바이트. PNG는 직접 인코딩하고(필터 0, RGBA 8비트), JPEG는 머리부만 있는 구조 확인용이다.</summary>
public static class TestImages
{
    /// <summary>w x h RGBA PNG. pixel(x, y)는 (r, g, b, a).</summary>
    public static byte[] Png(int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var raw = new byte[h * (1 + w * 4)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;   // 필터 없음
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                raw[o++] = r; raw[o++] = g; raw[o++] = b; raw[o++] = a;
            }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, w); WriteBe(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = 6;   // 8비트, RGBA
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", z.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>40x20: 왼쪽 반은 완전 불투명 빨강, 오른쪽 반은 완전 투명.</summary>
    public static byte[] HalfTransparentRed()
        => Png(40, 20, (x, _) => x < 20 ? ((byte)220, (byte)30, (byte)30, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)0));

    /// <summary>60x30 기준 이미지용: 파랑→초록 가로 그러데이션에 가운데 투명 원.</summary>
    public static byte[] GradientWithHole()
        => Png(60, 30, (x, y) =>
        {
            double dx = x - 29.5, dy = y - 14.5;
            if (dx * dx + dy * dy < 8 * 8) return ((byte)0, (byte)0, (byte)0, (byte)0);
            return ((byte)(30 + x), (byte)(60 + x * 2), (byte)(220 - x * 3), (byte)255);
        });

    /// <summary>SOI + APP0 + SOF0(w x h, 3성분) + EOI. 디코더에는 쓸 수 없고 PDF 구조(/DCTDecode로 그대로 들어가는지) 확인용.</summary>
    public static byte[] JpegHeaderOnly(int w, int h)
        =>
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
            0xFF, 0xD9,
        ];

    /// <summary>임의 색 유형·비트 깊이·인터레이스의 PNG. raw는 필터 바이트가 붙은 스캔라인 전체(zlib 압축은 여기서 한다).</summary>
    public static byte[] PngRaw(int w, int h, byte colorType, byte bitDepth, byte interlace, byte[] raw, byte[]? plte = null)
    {
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, w); WriteBe(ihdr, 4, h);
        ihdr[8] = bitDepth; ihdr[9] = colorType; ihdr[12] = interlace;
        Chunk(ms, "IHDR", ihdr);
        if (plte is not null) Chunk(ms, "PLTE", plte);
        Chunk(ms, "IDAT", z.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>IHDR만 있는 잘린 PNG(크기 검사용: 디코딩은 불가능).</summary>
    public static byte[] PngHeaderOnly(int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, w); WriteBe(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(ms, "IHDR", ihdr);
        return ms.ToArray();
    }

    private static void WriteBe(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in t) crc = Crc(crc, b);
        foreach (byte b in data) crc = Crc(crc, b);
        crc ^= 0xFFFFFFFF;
        var c = new byte[4];
        WriteBe(c, 0, (int)crc);
        s.Write(c);
    }

    private static uint Crc(uint crc, byte b)
    {
        crc ^= b;
        for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        return crc;
    }
}
