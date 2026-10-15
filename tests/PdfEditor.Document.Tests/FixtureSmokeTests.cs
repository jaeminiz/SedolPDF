using Pdf.Native;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests;

/// <summary>TestPdfFactory가 만든 파일이 PDFium으로 열리는지 확인한다.</summary>
public class FixtureSmokeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-fixtures-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public FixtureSmokeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private Task<int> PageCountAsync(string path) =>
        _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(_t, path);
            return doc.PageCount;
        });

    [Fact]
    public async Task CreateLabeled_OpensWithPageCount()
    {
        var p = P("labeled.pdf");
        TestPdfFactory.CreateLabeled(p, [(595, 842), (842, 595), (300, 400)]);
        Assert.Equal(3, await PageCountAsync(p));
    }

    [Fact]
    public async Task CreateKoreanText_Opens()
    {
        var p = P("ko.pdf");
        TestPdfFactory.CreateKoreanText(p, "한글 텍스트 시험");
        Assert.Equal(1, await PageCountAsync(p));
    }

    [Fact]
    public async Task CreateWithOutline_Opens()
    {
        var p = P("outline.pdf");
        TestPdfFactory.CreateWithOutline(p);
        Assert.Equal(3, await PageCountAsync(p));
    }

    [Fact]
    public async Task CreateWithAttachment_Opens()
    {
        var p = P("att.pdf");
        TestPdfFactory.CreateWithAttachment(p, "a.txt", [1, 2, 3]);
        Assert.Equal(1, await PageCountAsync(p));
    }

    [Fact]
    public async Task CreateWithLayers_Opens()
    {
        var p = P("layers.pdf");
        TestPdfFactory.CreateWithLayers(p, ["Layer A", "Layer B"]);
        Assert.Equal(1, await PageCountAsync(p));
    }

    [Fact]
    public async Task CreateWithCropBox_Opens()
    {
        var p = P("crop.pdf");
        TestPdfFactory.CreateWithCropBox(p, (50, 60, 400, 700));
        Assert.Equal(1, await PageCountAsync(p));
    }

    [Fact]
    public async Task CreateOwnerRestricted_OpensWithoutPassword()
    {
        var p = P("owner.pdf");
        TestPdfFactory.CreateOwnerRestricted(p, allowPrint: false, allowModify: false, allowCopy: false);
        Assert.Equal(1, await PageCountAsync(p));
    }

    [Fact]
    public async Task AddTextAnnotation_Opens()
    {
        var p = P("annot.pdf");
        TestPdfFactory.CreateBlank(p, 2, 595, 842);
        TestPdfFactory.AddTextAnnotation(p, 1);
        Assert.Equal(2, await PageCountAsync(p));
    }

    [Fact]
    public async Task CorruptXref_StillOpensViaRecovery()
    {
        var p = P("corrupt.pdf");
        TestPdfFactory.CreateBlank(p, 2, 595, 842);
        TestPdfFactory.CorruptXref(p);
        Assert.Equal(2, await PageCountAsync(p));
    }
}
