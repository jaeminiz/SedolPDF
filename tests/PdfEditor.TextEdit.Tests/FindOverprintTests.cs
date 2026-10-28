using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using PdfEditor.TextEdit.FindReplace;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// 0.4.0 실제 도면 결함: 굵게 보이려고 같은 글을 조금 비껴 두 번 찍은 도면에서 두 벌의 글자가 x 순서로 섞여("VVIISS") 낱말을 못 찾거나 한 벌만 바뀌어
/// 화면에 옛 글자가 비쳤다. 겹쳐 찍은 글은 층으로 나누어 각각 찾고 바꾼다.
/// </summary>
public sealed class FindOverprintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v040-overprint-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThreadHolder _h = new();
    private sealed class PdfiumThreadHolder : IDisposable { public readonly Pdf.Native.PdfiumThread T = new(); public void Dispose() => T.Dispose(); }

    static FindOverprintTests() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TestPdfFactory).TypeHandle);

    public void Dispose() { _h.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private string Make(double offsetPt, bool opposite = false)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, $"op-{offsetPt}.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage(); page.Width = XUnit.FromPoint(595); page.Height = XUnit.FromPoint(842);
            var f = new XFont("Arial", 14, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString("0123456789 VIS SENSOR UTL", f, XBrushes.Black, new XPoint(100, 740));
            // 같은 글을 두 번(두 번째는 조금 비껴서)
            g.DrawString("VIS SENSOR", f, XBrushes.Black, new XPoint(100, 600));
            g.DrawString("VIS SENSOR", f, XBrushes.Black, new XPoint(100 + offsetPt, 600));
            g.DrawString("TEMP DETECTOR", f, XBrushes.Black, new XPoint(100, 570));
            doc.Save(path);
        }
        return path;
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.6)]
    [InlineData(1.2)]
    [InlineData(2.5)]
    public void OverprintedText_BothCopiesAreFoundWhole(double offset)
    {
        using var doc = PdfReader.Open(Make(offset), PdfDocumentOpenMode.Import);
        var idx = PageTextIndex.Build(doc.Pages[0], 0);
        foreach (var q in new[] { "VIS", "SENSOR", "VIS SENSOR" })
        {
            int inBody = idx.Find(new FindOptions { Query = q, MatchCase = true }).Count(m => m.Bounds.B is > 200 and < 280);
            Assert.True(inBody == 2, $"offset {offset} '{q}': {inBody} (두 벌이어야 함)");
        }
    }

    [Fact]
    public async Task OverprintedText_ReplaceBothCopies_SaveAndReopen_NoOldTextLeft()
    {
        string src = Make(0.8);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        var font = AppFont.Load(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
        var cat = AppFontCatalog.ForBaseDirectory(font, dir.FullName);
        var s = await DocumentSession.OpenAsync(_h.T, src, null);
        try
        {
            using var found = await FindReplaceService.FindAsync(s, new FindOptions { Query = "VIS", MatchCase = true });
            var matches = found.Matches.Where(m => m.Bounds.B is > 200 and < 280).ToList();
            Assert.Equal(2, matches.Count);
            var batch = await FindReplaceService.PlanAsync(s, found, matches, "UTL", font, cat);
            Assert.All(batch.Items, i => Assert.True(i.Ok, i.Refusal.ToString()));
            await s.Commands.ExecuteAsync(FindReplaceService.BuildCommand(s, batch)!);
            string saved = Path.Combine(_dir, "saved.pdf");
            await new SaveService([], null, new PdfSharpAnnotationPatcher(font) { SerifFont = () => cat.Serif }, s.Assets).SaveAsync(s, saved);
            var s2 = await DocumentSession.OpenAsync(_h.T, saved, null);
            try
            {
                using var again = await FindReplaceService.FindAsync(s2, new FindOptions { Query = "VIS", MatchCase = true });
                Assert.DoesNotContain(again.Matches, m => m.Bounds.B is > 200 and < 280);   // 옛 글이 두 벌 다 사라졌다
                using var nw = await FindReplaceService.FindAsync(s2, new FindOptions { Query = "UTL", MatchCase = true });
                Assert.Equal(2, nw.Matches.Count(m => m.Bounds.B is > 200 and < 280));
            }
            finally { await s2.DisposeAsync(); }
        }
        finally { await s.DisposeAsync(); }
    }
}
