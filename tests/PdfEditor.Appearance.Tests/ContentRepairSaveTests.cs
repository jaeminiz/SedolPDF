using Pdf.Native;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using NativeDoc = Pdf.Native.PdfDocument;

namespace PdfEditor.Appearance.Tests;

/// <summary>0.10.0 Task 2: 저장할 때 망가진 쪽 내용을 고치고, 고친 쪽 번호를 알린다.</summary>
public sealed class ContentRepairSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-repairsave-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly PdfSharpAnnotationPatcher _patcher = new(() => []);

    public ContentRepairSaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        _patcher.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private byte[] Render(string path) => _t.InvokeAsync(() =>
    {
        using var d = NativeDoc.Open(_t, path, null);
        using var p = d.LoadPage(0);
        return p.RenderTile(1, 0, 0, 595, 842, false);
    }).Result;

    private static double DiffRatio(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i += 4) if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 64) n++;
        return n / (a.Length / 4.0);
    }

    [Fact]
    public void DamagedPage_RepairContent_ReturnsPageZero_AndRendersLikeOriginal()
    {
        DamagedContentPdfFactory.Create(P("good.pdf"), damaged: false);
        DamagedContentPdfFactory.Create(P("bad.pdf"), damaged: true);
        var good = Render(P("good.pdf"));
        Assert.True(DiffRatio(good, Render(P("bad.pdf"))) > 0.0001, "damaged file must look different before repair");
        var pages = _patcher.RepairContent(P("bad.pdf"), null);
        Assert.Equal([0], pages);
        Assert.True(DiffRatio(good, Render(P("bad.pdf"))) <= 0.001);
    }

    [Fact]
    public void BalancedFile_RepairContent_ReturnsEmpty_FileUntouched()
    {
        DamagedContentPdfFactory.Create(P("good.pdf"), damaged: false);
        var before = File.ReadAllBytes(P("good.pdf"));
        Assert.Empty(_patcher.RepairContent(P("good.pdf"), null));
        Assert.Equal(before, File.ReadAllBytes(P("good.pdf")));
    }

    [Fact]
    public async Task SaveService_LastRepairedPages_IsSet()
    {
        DamagedContentPdfFactory.Create(P("bad.pdf"), damaged: true);
        await using var s = await DocumentSession.OpenAsync(_t, P("bad.pdf"), null);
        var svc = new SaveService([], null, _patcher);
        await svc.SaveAsync(s, P("bad.pdf"));
        Assert.Equal([0], svc.LastRepairedPages);
        await svc.SaveAsync(s, P("bad.pdf"));
        Assert.Empty(svc.LastRepairedPages);
    }
}
