using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Optimize.Tests;

public class LosslessCleanerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-clean-" + Guid.NewGuid().ToString("N"));

    public LosslessCleanerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private static int ImageObjectCount(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        var ids = new HashSet<int>();
        foreach (PdfPage page in doc.Pages)
        {
            var xo = page.Resources?.Elements.GetDictionary("/XObject");
            if (xo is null) continue;
            foreach (var key in xo.Elements.Keys)
                if (xo.Elements.GetDictionary(key) is { } d && d.Elements.GetName("/Subtype") == "/Image") ids.Add(d.Reference!.ObjectNumber);
        }
        return ids.Count;
    }

    private static int PageCount(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    [Fact]
    public void Clean_DropsUnreferencedObjects_FromIncrementalUpdates()
    {
        string src = P("src.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(200, 150, ImageKind.FlateRgb, 200, 150));
        byte[] before = PdfAppend.UnreferencedObject(File.ReadAllBytes(src), 200_000);
        Assert.Equal(1, PageCount(before));   // 덧붙인 파일도 열린다
        byte[] after = LosslessCleaner.Clean(before);
        Assert.True(after.Length < before.Length - 150_000, $"unreferenced object should be dropped: {before.Length} -> {after.Length}");
        Assert.Equal(1, PageCount(after));
        Assert.Equal(1, ImageObjectCount(after));
    }

    [Fact]
    public void Clean_KeepsPageCountAndImages()
    {
        string src = P("three.pdf");
        ImagePdfFactory.Create(src,
            new ImageSpec(120, 90, ImageKind.FlateRgb, 120, 90),
            new ImageSpec(120, 90, ImageKind.FlateGray, 120, 90),
            new ImageSpec(120, 90, ImageKind.Jpeg, 120, 90));
        byte[] before = File.ReadAllBytes(src);
        byte[] after = LosslessCleaner.Clean(before);
        Assert.Equal(3, PageCount(after));
        Assert.Equal(ImageObjectCount(before), ImageObjectCount(after));
    }
}

public class ContentRecompressionTests
{
    private static byte[] Deflate(byte[] raw, System.IO.Compression.CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, level, true)) z.Write(raw);
        return ms.ToArray();
    }

    private static string Commands(int seed, int count)
    {
        var rnd = new Random(seed);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++) sb.Append($"{rnd.Next(0, 500)}.{rnd.Next(10, 99)} {rnd.Next(0, 700)}.{rnd.Next(10, 99)} m {rnd.Next(0, 500)} 100 l S\n");
        return sb.ToString();
    }

    private static byte[] TwoWeaklyCompressedStreams(string a, string b)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        foreach (var text in new[] { a, b })
        {
            var c = page.Contents.AppendContent();
            c.Elements.SetName("/Filter", "/FlateDecode");
            c.CreateStream(Deflate(System.Text.Encoding.ASCII.GetBytes(text), System.IO.Compression.CompressionLevel.Fastest));
        }
        doc.Options.CompressContentStreams = false;
        using var o = new MemoryStream();
        doc.Save(o, closeStream: false);
        return o.ToArray();
    }

    [Fact]
    public void Clean_MergesPageContentsAndRecompressesSmaller_WithSameCommands()
    {
        string a = Commands(1, 4000), b = Commands(2, 4000);
        byte[] before = TwoWeaklyCompressedStreams(a, b);
        byte[] after = LosslessCleaner.Clean(before);

        Assert.True(after.Length < before.Length * 0.97, $"{before.Length} -> {after.Length}");
        using var ms = new MemoryStream(after);
        using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        var contents = doc.Pages[0].Contents;
        Assert.Single(contents.Elements.Items);
        string decoded = System.Text.Encoding.ASCII.GetString(((PdfDictionary)((PdfReference)contents.Elements.Items[0]).Value).Stream.UnfilteredValue);
        string[] expected = (a + b).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected, decoded.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
}
