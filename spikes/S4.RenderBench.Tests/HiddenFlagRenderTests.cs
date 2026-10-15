using Pdf.Native;

namespace S4.RenderBench.Tests;

public class HiddenFlagRenderTests : IDisposable
{
    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "s4-" + Guid.NewGuid().ToString("N"));

    public HiddenFlagRenderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // 렌더 영역: 페이지 위쪽 (y 0~400) 전체. Square(빨강)는 PDF y 600~650 → 화면 y 192~242, Squiggly(파랑)는 PDF y 500~540.
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

    [Fact]
    public async Task HiddenFlag_HidesSupportedType_KeepsUnsupported()
    {
        var path = Path.Combine(_dir, "mixed.pdf");
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.CreateNew(_t);
            doc.AddPage(595, 842);
            SampleAnnots.AddSquareAndSquiggly(doc, 0);
            doc.SaveCopy(path);
        });

        var (normalRed, normalBlue, hiddenRed, hiddenBlue, restoredFlags) = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            var normal = CountColors(page.RenderTile(1.0, 0, 0, 595, 400, true));
            var hiddenCounts = default((int red, int blue));
            var flagsBefore = SampleAnnots.GetFlags(page);
            var flagsAfter = HiddenFlagRenderer.RenderWithSupportedHidden(page, 595, 400, FpdfAnnotSubtype.Square, out var px);
            hiddenCounts = CountColors(px);
            return (normal.red, normal.blue, hiddenCounts.red, hiddenCounts.blue, flagsBefore.SequenceEqual(flagsAfter));
        });

        Assert.True(normalRed > 50, $"기준: 빨강 {normalRed}");
        Assert.True(normalBlue > 50, $"기준: 파랑 {normalBlue}");
        Assert.Equal(0, hiddenRed);
        Assert.True(hiddenBlue > 50, $"숨김 렌더에서 파랑 {hiddenBlue}");
        Assert.True(restoredFlags, "렌더 후 플래그가 원래대로 복원되어야 함");
    }

    [Fact]
    public async Task HiddenFlag_RestoredFlags_NotWrittenOnSave()
    {
        var path = Path.Combine(_dir, "orig.pdf");
        var path2 = Path.Combine(_dir, "resaved.pdf");
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.CreateNew(_t);
            doc.AddPage(595, 842);
            SampleAnnots.AddSquareAndSquiggly(doc, 0);
            doc.SaveCopy(path);
        });

        var (before, after) = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            var b = SampleAnnots.GetFlags(page);
            HiddenFlagRenderer.RenderWithSupportedHidden(page, 595, 400, FpdfAnnotSubtype.Square, out _);
            doc.SaveCopy(path2);
            using var doc2 = PdfDocument.Open(_t, path2);
            using var page2 = doc2.LoadPage(0);
            return (b, SampleAnnots.GetFlags(page2));
        });

        Assert.Equal(before, after);
        Assert.Equal(0, after[0] & 2);   // Hidden 비트(2)가 저장 파일에 남지 않아야 함
    }
}
