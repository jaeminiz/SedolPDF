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

    /// <summary>Acrobat처럼 증분 저장을 흉내: 끝에 어디서도 참조하지 않는 큰 스트림 객체와 새 xref·trailer를 덧붙인다.</summary>
    private static byte[] AppendUnreferencedObject(byte[] pdf, int junkBytes)
    {
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        long prev = long.Parse(System.Text.RegularExpressions.Regex.Match(text[(sx + 9)..], @"\d+").Value);
        string trailer = text[text.LastIndexOf("trailer", StringComparison.Ordinal)..];
        int size = int.Parse(System.Text.RegularExpressions.Regex.Match(trailer, @"/Size\s+(\d+)").Groups[1].Value);
        string root = System.Text.RegularExpressions.Regex.Match(trailer, @"/Root\s+(\d+ \d+ R)").Groups[1].Value;

        using var ms = new MemoryStream();
        ms.Write(pdf);
        long objOffset = ms.Length + 1;
        void W(string s) => ms.Write(System.Text.Encoding.Latin1.GetBytes(s));
        W($"\n{size} 0 obj\n<< /Length {junkBytes} >>\nstream\n");
        ms.Write(new byte[junkBytes].Select((_, i) => (byte)(i * 31 % 251)).ToArray());
        W("\nendstream\nendobj\n");
        long xrefOffset = ms.Length;
        W($"xref\n{size} 1\n{objOffset:D10} 00000 n \ntrailer\n<< /Size {size + 1} /Root {root} /Prev {prev} >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Clean_DropsUnreferencedObjects_FromIncrementalUpdates()
    {
        string src = P("src.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(200, 150, ImageKind.FlateRgb, 200, 150));
        byte[] before = AppendUnreferencedObject(File.ReadAllBytes(src), 200_000);
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
