using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.Appearance.Patching;

/// <summary>
/// 그림 스탬프의 이미지를 PDF 이미지 XObject로 넣는다.
/// PNG는 PDFsharp <see cref="XImage"/>로 읽어 /FlateDecode로 넣고, 알파 채널은 /SMask로 분리된다.
/// JPEG는 다시 인코딩하지 않고 원본 바이트를 /DCTDecode 스트림으로 그대로 둔다.
/// </summary>
internal static class ImageEmbedder
{
    public static PdfReference Embed(PdfSharp.Pdf.PdfDocument doc, byte[] data, string format)
        => format == "jpeg" ? EmbedJpeg(doc, data) : EmbedPng(doc, data);

    private static PdfReference EmbedPng(PdfSharp.Pdf.PdfDocument doc, byte[] data)
    {
        // PDFsharp은 이미지 XObject를 만드는 공개 API가 XGraphics.DrawImage뿐이다: 임시 XForm에 한 번 그려 만들어진 XObject를 가져온다.
        using var ms = new MemoryStream(data, writable: false);
        using var image = XImage.FromStream(ms);
        var before = new HashSet<PdfObject>(doc.Internals.GetAllObjects());
        var form = new XForm(doc, XUnit.FromPoint(image.PointWidth), XUnit.FromPoint(image.PointHeight));
        using (var gfx = XGraphics.FromForm(form))
            gfx.DrawImage(image, 0, 0, image.PointWidth, image.PointHeight);

        // 새로 생긴 이미지 중 다른 이미지의 /SMask가 아닌 것이 본 이미지다.
        var created = doc.Internals.GetAllObjects()
            .Where(o => !before.Contains(o) && o is PdfDictionary d && d.Elements.GetName("/Subtype") == "/Image")
            .Cast<PdfDictionary>().ToList();
        var masks = new HashSet<PdfObject>();
        foreach (var d in created)
            if (Dict(d.Elements["/SMask"]) is { } m) masks.Add(m);
        var main = created.FirstOrDefault(d => !masks.Contains(d))
            ?? throw new InvalidOperationException("PDFsharp did not produce an image XObject for the PNG.");
        return main.Reference!;
    }

    private static PdfDictionary? Dict(PdfItem? item) => item switch
    {
        PdfReference r => r.Value as PdfDictionary,
        PdfDictionary d => d,
        _ => null,
    };

    private static PdfReference EmbedJpeg(PdfSharp.Pdf.PdfDocument doc, byte[] data)
    {
        var (w, h, comps, adobe) = ReadJpegHeader(data);
        var img = new PdfDictionary(doc);
        img.Elements["/Type"] = new PdfName("/XObject");
        img.Elements["/Subtype"] = new PdfName("/Image");
        img.Elements["/Width"] = new PdfInteger(w);
        img.Elements["/Height"] = new PdfInteger(h);
        img.Elements["/BitsPerComponent"] = new PdfInteger(8);
        img.Elements["/ColorSpace"] = new PdfName(comps switch { 1 => "/DeviceGray", 4 => "/DeviceCMYK", _ => "/DeviceRGB" });
        if (comps == 4 && adobe)
        {
            // Adobe가 만든 CMYK JPEG는 값이 뒤집혀 저장된다.
            var decode = new PdfArray(doc);
            foreach (int v in new[] { 1, 0, 1, 0, 1, 0, 1, 0 }) decode.Elements.Add(new PdfInteger(v));
            img.Elements["/Decode"] = decode;
        }
        img.CreateStream(data);
        img.Elements["/Filter"] = new PdfName("/DCTDecode");
        doc.Internals.AddObject(img);
        return img.Reference!;
    }

    /// <summary>JPEG의 SOFn 표지에서 폭·높이·성분 수, APP14 "Adobe" 표지 유무를 읽는다.</summary>
    private static (int W, int H, int Components, bool Adobe) ReadJpegHeader(byte[] d)
    {
        bool adobe = false;
        int i = 2;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            byte m = d[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
            int len = (d[i + 2] << 8) | d[i + 3];
            if (m == 0xEE && len >= 7 && i + 8 <= d.Length && d[i + 4] == 'A' && d[i + 5] == 'd' && d[i + 6] == 'o' && d[i + 7] == 'b')
                adobe = true;
            if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
            {
                if (i + 10 > d.Length) break;
                int h = (d[i + 5] << 8) | d[i + 6], w = (d[i + 7] << 8) | d[i + 8];
                return (w, h, d[i + 9], adobe);
            }
            if (len < 2) break;
            i += 2 + len;
        }
        throw new InvalidDataException("Not a valid JPEG (no frame header).");
    }
}
