using System.Buffers.Binary;
using System.IO.Compression;

namespace PdfEditor.Document.Content;

/// <summary>
/// 쪽에 넣을 PNG를 BGRA로 푼다(M4 C5 — PDFium 이미지 객체는 비트맵을 받는다). 8비트, 비월(interlace) 아님,
/// 색 형식 회색(0)·RGB(2)·팔레트(3)·회색+알파(4)·RGBA(6)만. 나머지는 <see cref="NotSupportedException"/>(넣을 때 거절).
/// 알파는 흰 배경에 미리 합성한다(PDFium SetBitmap은 알파를 /SMask로 쓰지만 결과를 단순하게 한다 — 검증 필요: 투명 PNG 표현).
/// </summary>
public static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>풀 수 있는 PNG인지(넣기 전 검사). 이유는 영어 기술 문자열.</summary>
    public static string? Unsupported(byte[] png)
    {
        try { var h = Header(png); return h.Depth != 8 ? "only 8-bit PNG" : h.Interlace != 0 ? "interlaced PNG" : h.Color is 0 or 2 or 3 or 4 or 6 ? null : "unknown PNG color type"; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException) { return e.Message; }
    }

    public static (byte[] Bgra, int W, int H) Decode(byte[] png)
    {
        var h = Header(png);
        if (Unsupported(png) is { } why) throw new NotSupportedException(why);
        byte[]? plte = null, trns = null;
        using var idat = new MemoryStream();
        int pos = 8;
        while (pos + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (len < 0 || pos + 12 + len > png.Length) throw new InvalidDataException("bad PNG chunk");
            var data = png.AsSpan(pos + 8, len);
            if (type == "PLTE") plte = data.ToArray();
            else if (type == "tRNS") trns = data.ToArray();
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") break;
            pos += 12 + len;
        }
        int ch = h.Color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int stride = h.W * ch;
        var raw = new byte[(long)(stride + 1) * h.H > int.MaxValue ? throw new InvalidDataException("PNG too large") : (stride + 1) * h.H];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.ReadExactly(raw);
        Unfilter(raw, stride, h.H, ch);

        var bgra = new byte[h.W * h.H * 4];
        for (int y = 0; y < h.H; y++)
        {
            int row = y * (stride + 1) + 1;
            for (int x = 0; x < h.W; x++)
            {
                int s = row + x * ch;
                byte r, g, b, a = 255;
                switch (h.Color)
                {
                    case 0: r = g = b = raw[s]; break;
                    case 4: r = g = b = raw[s]; a = raw[s + 1]; break;
                    case 2: r = raw[s]; g = raw[s + 1]; b = raw[s + 2]; break;
                    case 3:
                        int i = raw[s];
                        if (plte is null || i * 3 + 2 >= plte.Length) throw new InvalidDataException("bad PNG palette");
                        r = plte[i * 3]; g = plte[i * 3 + 1]; b = plte[i * 3 + 2];
                        if (trns is not null && i < trns.Length) a = trns[i];
                        break;
                    default: r = raw[s]; g = raw[s + 1]; b = raw[s + 2]; a = raw[s + 3]; break;
                }
                int d = (y * h.W + x) * 4;
                bgra[d] = Blend(b, a); bgra[d + 1] = Blend(g, a); bgra[d + 2] = Blend(r, a); bgra[d + 3] = 255;
            }
        }
        return (bgra, h.W, h.H);
    }

    private static byte Blend(byte c, byte a) => (byte)((c * a + 255 * (255 - a) + 127) / 255);

    private readonly record struct Hdr(int W, int H, int Depth, int Color, int Interlace);

    private static Hdr Header(byte[] png)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("not a PNG");
        int w = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), h = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        if (w <= 0 || h <= 0 || (long)w * h > 50_000_000) throw new InvalidDataException("PNG size out of range");
        return new Hdr(w, h, png[24], png[25], png[28]);
    }

    private static void Unfilter(byte[] raw, int stride, int height, int bpp)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1), prev = row - (stride + 1);
            int f = raw[row];
            for (int x = 0; x < stride; x++)
            {
                int i = row + 1 + x;
                int a = x >= bpp ? raw[i - bpp] : 0;
                int b = y > 0 ? raw[prev + 1 + x] : 0;
                int c = x >= bpp && y > 0 ? raw[prev + 1 + x - bpp] : 0;
                raw[i] = f switch
                {
                    0 => raw[i],
                    1 => (byte)(raw[i] + a),
                    2 => (byte)(raw[i] + b),
                    3 => (byte)(raw[i] + ((a + b) >> 1)),
                    4 => (byte)(raw[i] + Paeth(a, b, c)),
                    _ => throw new InvalidDataException("bad PNG filter"),
                };
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
