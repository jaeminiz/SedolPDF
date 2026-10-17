using System.IO.Compression;
using PdfEditor.Appearance.Patching;
using PdfEditor.TestFixtures;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Xunit.Abstractions;

namespace PdfEditor.Appearance.Tests;

/// <summary>신뢰할 수 없는 이미지·첨부의 크기 상한과 추가 시점 검증 (M3a Task 10 fix round 1).</summary>
public class AssetHardeningTests(ITestOutputHelper output)
{
    private static AnnotationAssets Validating() => new(ImageEmbedder.Validate);

    // ---- 화소 상한 ----

    [Fact]
    public void AddImage_RejectsTooManyTotalPixels_EvenWithinPerAxisCap()
    {
        var assets = new AnnotationAssets();
        // 10000 x 10000 = 100 MP: 한 변 상한(16384)은 통과하지만 전체 화소 상한(50 MP)을 넘는다.
        var ex = Assert.Throws<ArgumentException>(() => assets.AddImage(TestImages.PngHeaderOnly(10000, 10000)));
        Assert.Contains("megapixel", ex.Message);
        // 16384 x 16384 (268 MP)도 같다.
        Assert.Throws<ArgumentException>(() => assets.AddImage(TestImages.PngHeaderOnly(16384, 16384)));
        // 상한 이하(7000 x 7000 = 49 MP)는 크기 검사를 통과한다.
        Assert.Equal(16, assets.AddImage(TestImages.PngHeaderOnly(7000, 7000)).Length);
        Assert.True(7000L * 7000 <= AnnotationAssets.MaxImageTotalPixels);
    }

    // ---- PNG 변형: 추가할 때 깨끗이 거절되거나 올바르게 들어간다 ----

    private static byte[] Rows(int w, int h, int bytesPerPixel, Func<int, int, byte[]> pixel)
    {
        var raw = new byte[h * (1 + w * bytesPerPixel)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < w; x++) { var p = pixel(x, y); foreach (byte b in p) raw[o++] = b; }
        }
        return raw;
    }

    public static IEnumerable<object[]> PngVariants()
    {
        // 팔레트 8비트
        yield return ["palette8", TestImages.PngRaw(4, 4, 3, 8, 0, Rows(4, 4, 1, (x, y) => [(byte)((x + y) % 2)]), [255, 0, 0, 0, 0, 255])];
        // 회색+알파 8비트
        yield return ["gray_alpha8", TestImages.PngRaw(4, 4, 4, 8, 0, Rows(4, 4, 2, (x, y) => [(byte)(x * 60), (byte)(y * 60)]))];
        // RGBA 16비트
        yield return ["rgba16", TestImages.PngRaw(4, 4, 6, 16, 0, Rows(4, 4, 8, (x, y) => [(byte)(x * 60), 0, 0, 0, 0, 0, 255, 255]))];
        // 인터레이스 RGB 8비트 (1x1: Adam7 첫 패스에 화소 하나)
        yield return ["interlaced_rgb", TestImages.PngRaw(1, 1, 2, 8, 1, [0, 200, 100, 50])];
        // 회색 8비트 (알파 없음)
        yield return ["gray8", TestImages.PngRaw(4, 4, 0, 8, 0, Rows(4, 4, 1, (x, y) => [(byte)(x * 60)]))];
        // 팔레트 4비트
        yield return ["palette4", TestImages.PngRaw(4, 2, 3, 4, 0, [0, 0x01, 0x10, 0, 0x10, 0x01], [255, 0, 0, 0, 0, 255])];
    }

    [Theory]
    [MemberData(nameof(PngVariants))]
    public void AddImage_PngVariant_AcceptedAndEmbeddable_OrRejectedCleanlyAtAddTime(string name, byte[] png)
    {
        var assets = Validating();
        string? id = null;
        Exception? failure = null;
        try { id = assets.AddImage(png); }
        catch (Exception e) { failure = e; }
        output.WriteLine($"{name}: {(failure is null ? "accepted" : "rejected: " + failure.Message)}");

        if (failure is not null)
        {
            // 추가 시점에 ArgumentException으로만 거절되고 보관소에는 남지 않는다.
            Assert.IsType<ArgumentException>(failure);
            Assert.Contains("cannot be used in a PDF", failure.Message);
            return;
        }
        // 받아들였다면 실제 문서에 넣어도 예외가 없고 이미지 XObject가 생긴다.
        Assert.True(assets.HasImage(id!));
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var r = ImageEmbedder.Embed(doc, assets.GetImage(id!).Data, assets.GetImage(id!).Format);
        Assert.Equal("/Image", ((PdfDictionary)r.Value).Elements.GetName("/Subtype"));
    }

    [Fact]
    public void AddImage_UndecodablePng_RejectedAtAddTime_NotLater()
    {
        // 서명·IHDR은 맞지만 IDAT가 없는 PNG: 크기 검사는 통과하지만 PDFsharp이 못 읽는다.
        var assets = Validating();
        var ex = Assert.Throws<ArgumentException>(() => assets.AddImage(TestImages.PngHeaderOnly(8, 8)));
        Assert.Contains("cannot be used in a PDF", ex.Message);
        Assert.False(assets.HasImage("0000000000000000"));
    }

    [Fact]
    public void AddImage_GoodRgbaPng_StillAccepted()
    {
        var assets = Validating();
        string id = assets.AddImage(TestImages.HalfTransparentRed());
        Assert.True(assets.HasImage(id));
    }

    // ---- 첨부 파일: 선언 크기·압축 폭탄·이름 ----

    private static byte[] PdfWithAttachment(byte[] streamData, bool flate, int? declaredSize, string fileName)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        var stream = new PdfDictionary(doc);
        stream.Elements["/Type"] = new PdfName("/EmbeddedFile");
        if (declaredSize is { } n)
        {
            var pars = new PdfDictionary(doc);
            pars.Elements["/Size"] = new PdfInteger(n);
            stream.Elements["/Params"] = pars;
        }
        stream.CreateStream(streamData);
        if (flate) stream.Elements["/Filter"] = new PdfName("/FlateDecode");
        doc.Internals.AddObject(stream);
        var ef = new PdfDictionary(doc);
        ef.Elements["/F"] = stream.Reference!;
        var fs = new PdfDictionary(doc);
        fs.Elements["/Type"] = new PdfName("/Filespec");
        fs.Elements["/UF"] = new PdfString(fileName);
        fs.Elements["/EF"] = ef;
        doc.Internals.AddObject(fs);
        var annot = new PdfDictionary(doc);
        annot.Elements["/Type"] = new PdfName("/Annot");
        annot.Elements["/Subtype"] = new PdfName("/FileAttachment");
        var rect = new PdfArray(doc);
        foreach (int v in new[] { 10, 10, 30, 30 }) rect.Elements.Add(new PdfInteger(v));
        annot.Elements["/Rect"] = rect;
        annot.Elements["/FS"] = fs;
        doc.Internals.AddObject(annot);
        var annots = new PdfArray(doc);
        annots.Elements.Add(annot.Reference!);
        page.Elements["/Annots"] = annots;
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var o = new MemoryStream();
        using (var z = new ZLibStream(o, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw);
        return o.ToArray();
    }

    [Fact]
    public void ReadAttachment_NormalFlate_Works()
    {
        var data = System.Text.Encoding.UTF8.GetBytes(new string('x', 5000));
        var pdf = PdfWithAttachment(Deflate(data), true, data.Length, "a.txt");
        var r = AnnotationReader.ReadAttachment(pdf, null, 0, 0);
        Assert.Equal(data, r!.Value.Data);
    }

    [Fact]
    public void ReadAttachment_FlateBomb_ReturnsNull_WithoutInflatingEverything()
    {
        // 30 MB를 선언 없이 압축한 스트림(상한 25 MB 초과): 압축본은 수십 KB지만 풀면 상한을 넘는다.
        var bomb = Deflate(new byte[30 * 1024 * 1024]);
        Assert.True(bomb.Length < 200_000);
        var pdf = PdfWithAttachment(bomb, true, null, "bomb.bin");
        Assert.Null(AnnotationReader.ReadAttachment(pdf, null, 0, 0));
    }

    [Fact]
    public void ReadAttachment_DeclaredSizeOverCap_ReturnsNull()
    {
        var pdf = PdfWithAttachment(Deflate([1, 2, 3]), true, AnnotationAssets.MaxFileBytes + 1, "lie.bin");
        Assert.Null(AnnotationReader.ReadAttachment(pdf, null, 0, 0));
    }

    [Theory]
    [InlineData("..\\..\\Windows\\evil.exe", "evil.exe")]
    [InlineData("C:\\temp\\a.txt", "a.txt")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("name\u0007with\u0000ctrl.txt", "namewithctrl.txt")]
    [InlineData("....", "attachment")]
    [InlineData("a..b.txt", "a.b.txt")]
    [InlineData("", "attachment")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("Com1.tar.gz", "_Com1.tar.gz")]
    [InlineData("LPT9.pdf", "_LPT9.pdf")]
    [InlineData("aux ", "_aux")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("COM0.txt", "COM0.txt")]
    [InlineData("report‮gnp.exe", "reportgnp.exe")]
    [InlineData("a‎b‏c⁦d⁩.txt", "abcd.txt")]
    public void AttachmentNames_Sanitized(string input, string expected)
        => Assert.Equal(expected, AttachmentNames.Sanitize(input));

    [Fact]
    public void AttachmentNames_LongName_CappedKeepingExtension()
    {
        string r = AttachmentNames.Sanitize(new string('a', 500) + ".pdf");
        Assert.Equal(AttachmentNames.MaxLength, r.Length);
        Assert.EndsWith(".pdf", r);
        Assert.Equal(AttachmentNames.MaxLength, AttachmentNames.Sanitize(new string('b', 500)).Length);
        // 서로게이트 쌍이 잘리지 않는다
        string emoji = AttachmentNames.Sanitize(string.Concat(Enumerable.Repeat("😀", 150)) + ".txt");
        Assert.True(emoji.Length <= AttachmentNames.MaxLength);
        Assert.DoesNotContain('�', emoji);
        Assert.False(char.IsHighSurrogate(emoji[^5]));
    }

    [Fact]
    public void ReadAttachment_NameSanitized()
    {
        var data = new byte[] { 1, 2, 3 };
        var pdf = PdfWithAttachment(data, false, 3, "..\\..\\evil.exe");
        var r = AnnotationReader.ReadAttachment(pdf, null, 0, 0)!.Value;
        Assert.Equal("evil.exe", r.Name);
        Assert.Equal(data, r.Data);
    }
}

public class PngVariantPixelTests
{
    private static (PdfDictionary Image, byte[] Pixels, byte[]? Mask) Embedded(byte[] png)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var r = ImageEmbedder.Embed(doc, png, "png");
        var page = doc.AddPage();
        var res = new PdfDictionary(doc);
        var xo = new PdfDictionary(doc);
        xo.Elements["/Im1"] = r;
        res.Elements["/XObject"] = xo;
        page.Elements["/Resources"] = res;
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        ms.Position = 0;
        using var back = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        foreach (var o in back.Internals.GetAllObjects())
            if (o is PdfDictionary d && d.Elements.GetName("/Subtype") == "/Image" && !back.Internals.GetAllObjects().Any(x => x is PdfDictionary p && p.Elements["/SMask"] is PdfReference sm && ReferenceEquals(sm.Value, d)))
            {
                byte[]? mask = d.Elements["/SMask"] is PdfReference mr && mr.Value is PdfDictionary md ? md.Stream!.UnfilteredValue : null;
                return (d, d.Stream!.UnfilteredValue, mask);
            }
        throw new InvalidOperationException("no image");
    }

    [Fact]
    public void GrayAlpha_SplitIntoGrayAndSMask_Correctly()
    {
        var (d, px, mask) = Embedded(TestImages.PngRaw(4, 1, 4, 8, 0, [0, 10, 100, 20, 150, 30, 200, 40, 250]));
        Assert.Equal("/DeviceGray", d.Elements.GetName("/ColorSpace"));
        Assert.Equal([10, 20, 30, 40], px);
        Assert.Equal([100, 150, 200, 250], mask);
    }

    [Fact]
    public void Interlaced_Rgb_DecodedCorrectly()
    {
        var (d, px, _) = Embedded(TestImages.PngRaw(1, 1, 2, 8, 1, [0, 200, 100, 50]));
        Assert.Equal("/DeviceRGB", d.Elements.GetName("/ColorSpace"));
        Assert.Equal([200, 100, 50], px);
    }

    [Fact]
    public void Palette8_BecomesIndexed_WithIndicesKept()
    {
        var (d, px, _) = Embedded(TestImages.PngRaw(2, 1, 3, 8, 0, [0, 0, 1], [255, 0, 0, 0, 0, 255]));
        Assert.Contains("/Indexed", d.Elements["/ColorSpace"]!.ToString());
        Assert.Equal([0, 1], px);
    }
}
