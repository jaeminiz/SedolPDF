using System.Collections.Immutable;
using PdfEditor.ContentStream.Fonts;
using PdfEditor.TextEdit.Paragraphs;
using Pdf.Native;

namespace PdfEditor.TextEdit.Tests.Paragraphs;

/// <summary>M5 Task 4: 실제 글꼴 폭(코드북)으로 합성 샘플 문단의 줄바꿈이 재현되는가 — 같은 길이 낱말 하나를 바꾸면 그 줄만 다시 쓰이는가.</summary>
public sealed class ParagraphLayoutRealFontTests : IDisposable
{
    private readonly PdfiumThread _t = new();
    public void Dispose() => _t.Dispose();

    [Theory]
    [InlineData("doc-en-left")]
    [InlineData("doc-ko-left-wordwrap")]
    public void SameLengthWordChange_OnlyThatLineRewritten_WithRealWidths(string sampleName)
    {
        var s = ParagraphTestData.Sample(sampleName);
        var ps = ProbeStudy.Study(_t, "syn:" + s.Name, s.Path, 1)[0];
        var byRef = new Dictionary<ObjKey, TextStream>();
        var runs = new List<TextRun>();
        foreach (var (run, ts) in ps.RunStream)
        {
            var tr = ParagraphTestData.ToTextRun(run);
            runs.Add(tr);
            byRef[new ObjKey(tr.Ref)] = ts;
        }
        var obstacles = ps.Pd.Obstacles.Select(ParagraphTestData.ToObstacle).ToList();
        var lines = LineAssembler.Assemble(runs, obstacles);
        var models = ParagraphDetector.Partition(lines, obstacles).Where(m => m.IsParagraph && m.Lines.Length >= 3).ToList();
        Assert.NotEmpty(models);

        int tried = 0, exact = 0;
        var failures = new List<string>();
        foreach (var m in models)
        {
            var streams = m.Lines.SelectMany(l => l.Runs).Select(r => byRef[new ObjKey(r.Ref)]).ToList();
            var shown = streams.SelectMany(x => x.Pieces).Where(x => x.Bytes is not null).Select(x => (ReadOnlyMemory<byte>)x.Bytes!).ToList();
            var book = FontCodebook.TryBuild(streams[0].Font, shown);
            if (book is null) continue;
            var measure = new CodebookMeasure(book);
            string orig = ParagraphLayout.OriginalText(m);
            var opt = new LayoutOptions { PageBounds = new PdfRectF(0, 0, 595, 842) };

            // 무편집
            Assert.True(ParagraphLayout.Layout(m, orig, measure, null, opt).NoChange);

            for (int li = 1; li < m.Lines.Length - 1; li++)
            {
                string[] words = m.Lines[li].Text.Split(' ');
                string w = words.FirstOrDefault(x => x.Length >= 4 && x.Distinct().Count() > 2 && x.All(char.IsLetter)) ?? "";
                if (w.Length == 0) continue;
                string rev = new(w.Reverse().ToArray());
                if (rev == w) continue;
                int at = orig.IndexOf(w, StringComparison.Ordinal);
                // 이 줄 안의 같은 낱말(앞 줄에 같은 낱말이 있으면 건너뜀)
                int lineStart = orig.IndexOf(m.Lines[li].Text, StringComparison.Ordinal);
                if (lineStart < 0 || at != lineStart + m.Lines[li].Text.IndexOf(w, StringComparison.Ordinal)) continue;
                string edited = orig[..at] + rev + orig[(at + w.Length)..];
                var r = ParagraphLayout.Layout(m, edited, measure, null, opt);
                tried++;
                if (r.FirstRewritten == li && r.LastRewritten == li && r.Choice == FontChoice.Original && r.Lines.Length == m.Lines.Length) exact++;
                else failures.Add(Describe(sampleName, li, m, measure, r));
            }
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), $"m5-task4-{sampleName}.txt"), $"tried={tried} exact={exact}\n" + string.Join("\n", failures));
        Assert.True(tried >= 3, $"only {tried} edits tried");
        Assert.True(exact >= 0.9 * tried, $"{exact}/{tried}: {string.Join(" | ", failures.Take(5))}");
    }

    private static string Describe(string name, int li, ParagraphModel m, CodebookMeasure measure, ParagraphLayoutResult r)
    {
        string lines = string.Join(" / ", m.Lines.Select(l => l.Text + "=" + measure.Advance(l.Text, m.Style.SizePt).ToString("0.0") + "/ink" + l.Width.ToString("0.0")));
        return $"{name} line {li}: [{lines}] edited[{string.Join(" / ", r.Lines.Select(l => l.Text))}] box={m.Box.Right - m.Box.Left:0.0} rewritten {r.FirstRewritten}..{r.LastRewritten} choice {r.Choice} lines {r.Lines.Length}/{m.Lines.Length}";
    }

    private readonly record struct ObjKey(int Original, long Inserted)
    {
        public ObjKey(PdfEditor.Document.Content.ObjectRef r) : this(r.OriginalIndex, r.InsertedId) { }
    }
}
