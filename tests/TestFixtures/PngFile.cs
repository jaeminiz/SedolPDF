using System.IO.Compression;

namespace PdfEditor.TestFixtures;

/// <summary>테스트용 최소 PNG 입출력 (8비트 RGBA, 쓰기는 필터 0, 읽기는 필터 0~4). 픽셀 버퍼는 BGRA.</summary>
public static class PngFile
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static void WriteBgra(string path, byte[] bgra, int w, int h)
    {
        if (bgra.Length != w * h * 4) throw new ArgumentException("buffer size differs from w*h*4", nameof(bgra));
        var raw = new byte[h * (1 + w * 4)];
        for (int y = 0; y < h; y++)
        {
            int o = y * (1 + w * 4);
            raw[o] = 0;
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 4, d = o + 1 + x * 4;
                raw[d] = bgra[s + 2]; raw[d + 1] = bgra[s + 1]; raw[d + 2] = bgra[s]; raw[d + 3] = bgra[s + 3];
            }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);

        using var fs = File.Create(path);
        fs.Write(Signature);
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, w); WriteBe(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(fs, "IHDR", ihdr);
        Chunk(fs, "IDAT", z.ToArray());
        Chunk(fs, "IEND", []);
    }

    public static (byte[] Bgra, int W, int H) ReadBgra(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("not a PNG");
        int pos = 8, w = 0, h = 0;
        using var idat = new MemoryStream();
        while (pos + 8 <= data.Length)
        {
            int len = ReadBe(data, pos);
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            int body = pos + 8;
            if (type == "IHDR")
            {
                w = ReadBe(data, body); h = ReadBe(data, body + 4);
                if (data[body + 8] != 8 || data[body + 9] != 6 || data[body + 12] != 0)
                    throw new InvalidDataException("only 8-bit RGBA non-interlaced PNG is supported");
            }
            else if (type == "IDAT") idat.Write(data, body, len);
            else if (type == "IEND") break;
            pos = body + len + 4;
        }
        idat.Position = 0;
        using var zs = new ZLibStream(idat, CompressionMode.Decompress);
        int stride = w * 4;
        var raw = new byte[h * (1 + stride)];
        zs.ReadExactly(raw);

        var rgba = new byte[h * stride];
        for (int y = 0; y < h; y++)
        {
            int f = raw[y * (1 + stride)];
            int src = y * (1 + stride) + 1, dst = y * stride;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= 4 ? rgba[dst + i - 4] : 0;
                int b = y > 0 ? rgba[dst - stride + i] : 0;
                int c = i >= 4 && y > 0 ? rgba[dst - stride + i - 4] : 0;
                int x = raw[src + i];
                rgba[dst + i] = (byte)(f switch
                {
                    0 => x,
                    1 => x + a,
                    2 => x + b,
                    3 => x + ((a + b) >> 1),
                    4 => x + Paeth(a, b, c),
                    _ => throw new InvalidDataException("unknown PNG filter"),
                });
            }
        }
        var bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2]; bgra[i + 1] = rgba[i + 1]; bgra[i + 2] = rgba[i]; bgra[i + 3] = rgba[i + 3];
        }
        return (bgra, w, h);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static int ReadBe(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

    private static void WriteBe(byte[] d, int o, int v)
    {
        d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
    }

    private static void Chunk(Stream s, string type, byte[] body)
    {
        var len = new byte[4]; WriteBe(len, 0, body.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(body);
        uint crc = 0xFFFFFFFF;
        foreach (var b in t) crc = Step(crc, b);
        foreach (var b in body) crc = Step(crc, b);
        crc ^= 0xFFFFFFFF;
        var c = new byte[4]; WriteBe(c, 0, (int)crc); s.Write(c);
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Step(uint crc, byte b) => Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
}

public static class ImageCompare
{
    /// <summary>Compares two BGRA buffers: ratio of pixels whose worst channel difference exceeds channelTolerance, and the max channel difference.</summary>
    public static (double MismatchRatio, int MaxDiff) Compare(byte[] a, byte[] b, int w, int h, int channelTolerance)
    {
        if (a.Length != b.Length || a.Length != w * h * 4) return (1.0, 255);
        int bad = 0, max = 0;
        for (int p = 0; p < w * h; p++)
        {
            int worst = 0;
            for (int c = 0; c < 4; c++) worst = Math.Max(worst, Math.Abs(a[p * 4 + c] - b[p * 4 + c]));
            max = Math.Max(max, worst);
            if (worst > channelTolerance) bad++;
        }
        return ((double)bad / (w * h), max);
    }
}
