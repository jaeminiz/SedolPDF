using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Pdf.Native;
using Pdf.Native.Interop;

namespace S1.FreeTextKorean;

internal static unsafe partial class AnnotNative
{
    private const string Lib = "pdfium";
    [LibraryImport(Lib)] public static partial IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [LibraryImport(Lib)] public static partial void FPDFPage_CloseAnnot(IntPtr annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetRect(IntPtr annot, FsRectF* rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetStringValue(IntPtr annot, byte* key, ushort* value);
    public const int Square = 5;
}

internal static class TestSupport
{
    public static string OutDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdfEditor.sln"))) dir = dir.Parent;
        var o = Path.Combine(dir!.FullName, "spikes", "out");
        Directory.CreateDirectory(o);
        return o;
    }

    public static byte[] LoadFont()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var f = Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf");
            if (File.Exists(f)) return File.ReadAllBytes(f);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("NotoSansKR-Regular.otf");
    }

    /// <summary>PDFium으로 A4 한 장짜리 빈 PDF를 만든다. squareWithKey가 true면 사용자 키가 든 Square 주석도 넣는다.</summary>
    public static async Task CreateBase(PdfiumThread t, string path, bool squareWithKey)
    {
        await t.InvokeAsync(() => BuildBase(t, path, squareWithKey));
    }

    private static unsafe void BuildBase(PdfiumThread t, string path, bool squareWithKey)
    {
        {
            using var doc = PdfDocument.CreateNew(t);
            doc.AddPage(595, 842);
            if (squareWithKey)
            {
                using var page = doc.LoadPage(0);
                var annot = AnnotNative.FPDFPage_CreateAnnot(page.Handle, AnnotNative.Square);
                var r = new FsRectF { Left = 100, Bottom = 600, Right = 200, Top = 650 };
                AnnotNative.FPDFAnnot_SetRect(annot, &r);
                var key = Encoding.ASCII.GetBytes("PXC_Test\0");
                var val = "keep\0".Select(c => (ushort)c).ToArray();
                fixed (byte* k = key)
                fixed (ushort* v = val)
                    AnnotNative.FPDFAnnot_SetStringValue(annot, k, v);
                AnnotNative.FPDFPage_CloseAnnot(annot);
            }
            doc.SaveCopy(path);
        }
    }

    /// <summary>타일을 렌더하고 비흰색 픽셀 비율을 돌려준다. pngPath가 있으면 PNG로도 저장한다.</summary>
    public static async Task<double> NonWhiteRatio(PdfiumThread t, string pdf, int x, int y, int w, int h,
        bool annots, string? pngPath = null, double scale = 1.0)
    {
        var px = await t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(t, pdf);
            using var page = doc.LoadPage(0);
            return page.RenderTile(scale, x, y, w, h, annots);
        });
        if (pngPath is not null) File.WriteAllBytes(pngPath, Png.Encode(px, w, h));
        int non = 0;
        for (int i = 0; i < px.Length; i += 4)
            if (px[i] != 255 || px[i + 1] != 255 || px[i + 2] != 255) non++;
        return (double)non / (w * h);
    }
}

/// <summary>육안 확인용 최소 PNG 인코더 (BGRA 입력).</summary>
internal static class Png
{
    public static byte[] Encode(byte[] bgra, int w, int h)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                raw.WriteByte(bgra[i + 2]);
                raw.WriteByte(bgra[i + 1]);
                raw.WriteByte(bgra[i]);
            }
        }
        using var z = new MemoryStream();
        using (var d = new ZLibStream(z, CompressionLevel.Fastest, true)) d.Write(raw.ToArray());
        using var o = new MemoryStream();
        o.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BE(ihdr, 0, w);
        BE(ihdr, 4, h);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk(o, "IHDR", ihdr);
        Chunk(o, "IDAT", z.ToArray());
        Chunk(o, "IEND", []);
        return o.ToArray();
    }

    private static void BE(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static uint Crc(byte[] d)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in d)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
        }
        return ~c;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BE(len, 0, data.Length);
        s.Write(len);
        var td = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(td);
        var crc = new byte[4];
        BE(crc, 0, (int)Crc(td));
        s.Write(crc);
    }
}
