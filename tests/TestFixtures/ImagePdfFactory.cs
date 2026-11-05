using System.IO.Compression;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfEditor.TestFixtures;

public enum ImageKind { Jpeg, FlateRgb, FlateGray }

/// <summary>
/// 쪽 하나에 그리는 이미지. DrawWidthPt×DrawHeightPt는 쪽에 그려지는 크기(pt) — 픽셀 수와 비교해 유효 dpi가 정해진다.
/// SharedOnNextPage: 다음 쪽이 같은 이미지 XObject를 쓴다(그 쪽의 PixelW 등은 무시). WithSoftMask: 같은 크기 회색 /SMask를 단다.
/// </summary>
public sealed record ImageSpec(int PixelW, int PixelH, ImageKind Kind, double DrawWidthPt, double DrawHeightPt,
    bool SharedOnNextPage = false, bool WithSoftMask = false);

/// <summary>용량 줄이기 시험용: 쪽마다 이미지 하나를 그리는 PDF. 픽셀은 압축이 잘 안 되는 결정적 잡음이다(같은 입력이면 같은 바이트).</summary>
public static class ImagePdfFactory
{
    public static void Create(string path, params ImageSpec[] pages)
    {
        using var doc = new PdfDocument();
        PdfDictionary? shared = null;
        for (int i = 0; i < pages.Length; i++)
        {
            var spec = pages[i];
            var page = doc.AddPage();
            page.Width = new XUnitPt(612);
            page.Height = new XUnitPt(792);
            PdfDictionary img = i > 0 && pages[i - 1].SharedOnNextPage && shared is not null ? shared : shared = NewImage(doc, spec, 1000 + i);
            var res = page.Resources;   // 없으면 만든다
            var xo = new PdfDictionary(doc);
            xo.Elements["/Im0"] = img.Reference!;
            res.Elements["/XObject"] = xo;
            string ops = FormattableString.Invariant($"q {spec.DrawWidthPt:0.###} 0 0 {spec.DrawHeightPt:0.###} 50 50 cm /Im0 Do Q\n");
            page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(ops));
        }
        doc.Save(path);
    }

    private static PdfDictionary NewImage(PdfDocument doc, ImageSpec s, int seed)
    {
        var d = new PdfDictionary(doc);
        d.Elements["/Type"] = new PdfName("/XObject");
        d.Elements["/Subtype"] = new PdfName("/Image");
        d.Elements["/Width"] = new PdfInteger(s.PixelW);
        d.Elements["/Height"] = new PdfInteger(s.PixelH);
        d.Elements["/BitsPerComponent"] = new PdfInteger(8);
        byte[] data;
        switch (s.Kind)
        {
            case ImageKind.Jpeg:
                data = TestJpeg.Create(s.PixelW, s.PixelH, (x, y) => (Noise(x, y, 0, seed), Noise(x, y, 1, seed), Noise(x, y, 2, seed)), 90);
                d.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
                d.Elements["/Filter"] = new PdfName("/DCTDecode");
                break;
            case ImageKind.FlateRgb:
                data = Zlib(Raw(s.PixelW, s.PixelH, 3, seed));
                d.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
                d.Elements["/Filter"] = new PdfName("/FlateDecode");
                break;
            default:
                data = Zlib(Raw(s.PixelW, s.PixelH, 1, seed));
                d.Elements["/ColorSpace"] = new PdfName("/DeviceGray");
                d.Elements["/Filter"] = new PdfName("/FlateDecode");
                break;
        }
        d.CreateStream(data);
        doc.Internals.AddObject(d);
        if (s.WithSoftMask)
        {
            var m = new PdfDictionary(doc);
            m.Elements["/Type"] = new PdfName("/XObject");
            m.Elements["/Subtype"] = new PdfName("/Image");
            m.Elements["/Width"] = new PdfInteger(s.PixelW);
            m.Elements["/Height"] = new PdfInteger(s.PixelH);
            m.Elements["/BitsPerComponent"] = new PdfInteger(8);
            m.Elements["/ColorSpace"] = new PdfName("/DeviceGray");
            m.Elements["/Filter"] = new PdfName("/FlateDecode");
            m.CreateStream(Zlib(Raw(s.PixelW, s.PixelH, 1, seed + 7)));
            doc.Internals.AddObject(m);
            d.Elements["/SMask"] = m.Reference!;
        }
        return d;
    }

    private static byte[] Raw(int w, int h, int comps, int seed)
    {
        var raw = new byte[w * h * comps];
        int k = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < comps; c++) raw[k++] = Noise(x, y, c, seed);
        return raw;
    }

    private static byte[] Zlib(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }

    private static byte Noise(int x, int y, int c, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + c * 1274126177 + seed * 362437);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (byte)(h >> 8);
        }
    }
}
