using PdfEditor.Document.Annotations;

namespace PdfEditor.Document.Tests.Annotations;

public class AnnotationAssetsTests
{
    // 1x1 PNG
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // 3x2 JPEG 머리부만(SOI + SOF0): 크기 읽기용
    private static readonly byte[] Jpg =
    [
        0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x02, 0x00, 0x03, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        0xFF, 0xD9,
    ];

    [Fact]
    public void Assets_RejectsNonPngJpeg()
    {
        var a = new AnnotationAssets();
        Assert.Throws<ArgumentException>(() => a.AddImage("GIF89a"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => a.AddImage([]));
        Assert.Throws<ArgumentException>(() => a.AddImage(new byte[100]));
        Assert.Throws<ArgumentException>(() => a.AddImage(Png[..20]));
    }

    [Fact]
    public void AddImage_Png_IdIsHashPrefix_RoundTrips()
    {
        var a = new AnnotationAssets();
        string id = a.AddImage(Png);
        Assert.Equal(16, id.Length);
        Assert.Equal(id, a.AddImage(Png));
        var (data, w, h, fmt) = a.GetImage(id);
        Assert.Equal(Png, data);
        Assert.Equal((1, 1, "png"), (w, h, fmt));
    }

    [Fact]
    public void AddImage_Jpeg_ReadsSize()
    {
        var a = new AnnotationAssets();
        var (_, w, h, fmt) = a.GetImage(a.AddImage(Jpg));
        Assert.Equal((3, 2, "jpeg"), (w, h, fmt));
    }

    [Fact]
    public void AddFile_RoundTrips_UnknownIdThrows()
    {
        var a = new AnnotationAssets();
        string id = a.AddFile("a.txt", [1, 2, 3]);
        var (name, data) = a.GetFile(id);
        Assert.Equal("a.txt", name);
        Assert.Equal(new byte[] { 1, 2, 3 }, data);
        Assert.Throws<KeyNotFoundException>(() => a.GetFile("nope"));
        Assert.Throws<KeyNotFoundException>(() => a.GetImage("nope"));
    }

    [Fact]
    public void Empty_IsReadOnly()
    {
        Assert.Throws<InvalidOperationException>(() => AnnotationAssets.Empty.AddImage(Png));
        Assert.Throws<InvalidOperationException>(() => AnnotationAssets.Empty.AddFile("a", [1]));
    }

    [Fact]
    public void Concurrent_AddsAreSafe()
    {
        var a = new AnnotationAssets();
        Parallel.For(0, 200, i => a.AddFile("f" + i, [(byte)i]));
        Assert.Equal("f7", a.GetFile(a.AddFile("f7", [7])).Name);
    }
}
