using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
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
