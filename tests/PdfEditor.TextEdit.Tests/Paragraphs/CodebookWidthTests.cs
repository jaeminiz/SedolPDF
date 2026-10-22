using PdfEditor.ContentStream.Fonts;
using PdfEditor.TextEdit.Paragraphs;
using PdfEditor.TestFixtures;
using Pdf.Native;

namespace PdfEditor.TextEdit.Tests.Paragraphs;

/// <summary>M5 Task 3: 코드북 폭(<c>/Widths</c>·<c>/W</c>)이 PDFium이 그린 글 객체 폭과 맞는가(PDFium 글 경계 = 잉크라 좌우 여백 0.15 em 허용).</summary>
public sealed class CodebookWidthTests : IDisposable
{
    private readonly PdfiumThread _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public void WidthMatchesPdfium_Within1Percent()
    {
        int compared = 0, within = 0, strictFonts = 0;
        var bad = new List<string>();
        foreach (var s in ParagraphTestData.Samples())
        {
            foreach (var ps in ProbeStudy.Study(_t, "syn:" + s.Name, s.Path, 1))
            {
                foreach (var (run, ts) in ps.RunStream)
                {
                    var shown = ts.Pieces.Where(p => p.Bytes is not null).Select(p => (ReadOnlyMemory<byte>)p.Bytes!).ToList();
                    var book = FontCodebook.TryBuild(ts.Font, shown);
                    if (book is null) continue;
                    strictFonts++;
                    string text = book.Decode(shown.SelectMany(x => x.ToArray()).ToArray()).Trim();
                    if (text.Length == 0 || run.U1 - run.U0 < 1) continue;
                    var measure = new CodebookMeasure(book);
                    Assert.True(measure.CanRender(text, out _), $"{s.Name}: '{text}' not renderable by its own codebook");
                    double adv = measure.Advance(text, (float)ts.Tf) * run.Scale * ts.Tz / 100.0;
                    double geo = run.U1 - run.U0, err = Math.Abs(adv - geo), tol = Math.Max(1.0, 0.01 * geo) + 0.15 * run.Size;
                    compared++;
                    if (err <= tol) within++; else bad.Add($"{s.Name}: '{text[..Math.Min(20, text.Length)]}' adv={adv:0.0} geo={geo:0.0}");
                }
            }
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "m5-task3-width.txt"), $"compared={compared} within={within} codebookFonts={strictFonts} bad={bad.Count}");
        Assert.True(compared >= 30, $"compared only {compared} runs");
        Assert.True(within >= 0.97 * compared, $"{within}/{compared} within tolerance; bad: {string.Join(" | ", bad.Take(5))}");
    }
}
