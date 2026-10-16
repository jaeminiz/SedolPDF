namespace Pdf.Native.Tests;

public class RenderOptionsTests : IDisposable
{
    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-ro-" + Guid.NewGuid().ToString("N"));

    public RenderOptionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // 렌더 영역: 페이지 위쪽 (y 0~400). Square(빨강)는 PDF y 600~650, Squiggly(파랑)는 PDF y 500~540.
    private static (int red, int blue) CountColors(byte[] bgra)
    {
        int red = 0, blue = 0;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            if (r > 180 && g < 90 && b < 90) red++;
            if (b > 180 && r < 90 && g < 90) blue++;
        }
        return (red, blue);
    }

    private static readonly IReadOnlySet<int> HideSquare = new HashSet<int> { PdfAnnotSubtype.Square };

    private Task<string> MakeMixed() => _t.InvokeAsync(() =>
    {
        var path = Path.Combine(_dir, "mixed.pdf");
        using var doc = PdfDocument.CreateNew(_t);
        doc.AddPage(595, 842);
        AnnotTestHelper.AddSquareAndSquiggly(doc, 0);
        doc.SaveCopy(path);
        return path;
    });

    [Fact]
    public async Task HiddenFlag_HidesSupportedType_KeepsUnsupported()
    {
        var path = await MakeMixed();
        var r = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            var normal = CountColors(page.RenderTile(1.0, 0, 0, 595, 400, new RenderOptions(true)));
            var before = AnnotTestHelper.GetFlags(page);
            var hidden = CountColors(page.RenderTile(1.0, 0, 0, 595, 400, new RenderOptions(true, HideSquare)));
            var after = AnnotTestHelper.GetFlags(page);
            return (normal, hidden, restored: before.SequenceEqual(after));
        });

        Assert.True(r.normal.red > 50, $"기준: 빨강 {r.normal.red}");
        Assert.True(r.normal.blue > 50, $"기준: 파랑 {r.normal.blue}");
        Assert.Equal(0, r.hidden.red);
        Assert.True(r.hidden.blue > 50, $"숨김 렌더에서 파랑 {r.hidden.blue}");
        Assert.True(r.restored, "렌더 후 플래그가 원래대로 복원되어야 함");
    }

    [Fact]
    public async Task HiddenFlag_RestoredFlags_NotWrittenOnSave()
    {
        var path = await MakeMixed();
        var path2 = Path.Combine(_dir, "resaved.pdf");
        var (before, after) = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            var b = AnnotTestHelper.GetFlags(page);
            page.RenderTile(1.0, 0, 0, 595, 400, new RenderOptions(true, HideSquare));
            doc.SaveCopy(path2);
            using var doc2 = PdfDocument.Open(_t, path2);
            using var page2 = doc2.LoadPage(0);
            return (b, AnnotTestHelper.GetFlags(page2));
        });

        Assert.Equal(before, after);
        Assert.Equal(0, after[0] & 2);   // Hidden 비트(2)가 저장 파일에 남지 않아야 함
    }

    [Fact]
    public async Task HiddenFlag_EmptySet_BehavesLikeNormal()
    {
        var path = await MakeMixed();
        var (a, b) = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            return (CountColors(page.RenderTile(1.0, 0, 0, 595, 400, true)),
                    CountColors(page.RenderTile(1.0, 0, 0, 595, 400, new RenderOptions(true, new HashSet<int>()))));
        });
        Assert.Equal(a, b);
    }
}
