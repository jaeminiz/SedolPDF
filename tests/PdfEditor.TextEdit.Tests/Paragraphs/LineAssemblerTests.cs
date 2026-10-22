using Pdf.Native;
using PdfEditor.TextEdit.Paragraphs;
using static PdfEditor.TextEdit.Tests.Paragraphs.ParagraphTestData;

namespace PdfEditor.TextEdit.Tests.Paragraphs;

public sealed class LineAssemblerTests : IDisposable
{
    private readonly PdfiumThread _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public void Words_SplitIntoRuns_AssembledIntoLine()
    {
        // 낱말마다 글 객체 하나: 틈 3 pt(0.3 em)는 공백, 1 pt(0.1 em)는 낱말 안(글자 모양 단위 객체)
        var runs = new List<TextRun>
        {
            Run("The", 0, 700), Run("cat", 18, 700), Run("sat", 36, 700), Run("on", 54, 700), Run("ce", 65, 700),
        };
        var lines = LineAssembler.Assemble(runs);
        var line = Assert.Single(lines);
        Assert.Equal("The cat sat once", line.Text);
        Assert.Equal(5, line.Runs.Length);
        Assert.False(line.Mixed);
        Assert.Equal(0, line.U0, 3);
        Assert.Equal(700, line.Baseline, 3);
    }

    [Fact]
    public void BigGap_MakesSeparateCells()
    {
        var lines = LineAssembler.Assemble([Run("left", 0, 700), Run("right", 20 + 12, 700)]);   // 틈 12 pt = 1.2 em > 1.0 em
        Assert.Equal(2, lines.Length);
        Assert.Equal("left", lines[0].Text);
        Assert.Equal("right", lines[1].Text);
    }

    [Fact]
    public void BulletMarker_JoinsItsText_UpTo3Em()
    {
        var lines = LineAssembler.Assemble([Run("•", 0, 700, width: 4), Run("First item", 25, 700)]);   // 틈 21 pt = 2.1 em, 마커라 이어짐
        var line = Assert.Single(lines);
        Assert.Equal("• First item", line.Text);
    }

    [Fact]
    public void VerticalRuleBetweenCells_SplitsLine()
    {
        var runs = new List<TextRun> { Run("AB", 0, 700), Run("CD", 14, 700) };   // 틈 4 pt = 0.4 em(공백 틈)
        Assert.Single(LineAssembler.Assemble(runs));
        var rule = new Obstacle(new PdfRectF(11, 690, 11.5f, 712), ObstacleKind.Line);
        var split = LineAssembler.Assemble(runs, [rule]);
        Assert.Equal(2, split.Length);
    }

    [Fact]
    public void DuplicateOverlaidText_TreatedAsOne()
    {
        var a = Run("Bold shown twice", 50, 600);
        var b = Run("Bold shown twice", 50.4, 600);
        var lines = LineAssembler.Assemble([a, b]);
        var line = Assert.Single(lines);
        Assert.Single(line.Runs);
        Assert.Equal(a.Ref, line.Runs[0].Ref);
        Assert.Equal(b.Ref, Assert.Single(line.Duplicates));
        Assert.Equal(2, line.AllRefs.Count());           // 편집하면 둘 다 대상
    }

    [Fact]
    public void InvisibleText_NotEditable()
    {
        var lines = LineAssembler.Assemble([Run("hidden ocr layer", 0, 700, visible: false), Run("   ", 0, 600)]);
        Assert.Empty(lines);
    }

    [Fact]
    public void InvisibleText_InScannedSample_IsNotEditable()
    {
        var p = Read(_t, Sample("dwg-invisible-ocr"));
        Assert.Empty(p.Lines);
        Assert.Equal(8, p.Data.Hidden);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(30)]
    [InlineData(135)]
    public void NonRightAngleText_Ignored(int angle)
    {
        Assert.Empty(LineAssembler.Assemble([Run("slanted", 0, 700, angle: angle)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RightAngleText_AssembledInLocalCoordinates(int angle)
    {
        var lines = LineAssembler.Assemble([Run("alpha", 100, 300, angle: angle), Run("beta", 100 + 30, 300, angle: angle)]);
        var line = Assert.Single(lines);
        Assert.Equal("alpha beta", line.Text);
        Assert.Equal(angle, line.Angle);
        Assert.Equal(100, line.U0, 2);
        Assert.Equal(300, line.Baseline, 2);
    }

    [Fact]
    public void MixedStyleInLine_IsFlagged()
    {
        var lines = LineAssembler.Assemble([Run("plain", 0, 700), Run("Bold", 34, 700, font: "F1-Bold"), Run("tail", 60, 700)]);
        var line = Assert.Single(lines);
        Assert.True(line.Mixed);
        var sizeMixed = LineAssembler.Assemble([Run("small", 0, 700), Run("big", 34, 700, size: 14)]);
        Assert.True(Assert.Single(sizeMixed).Mixed);
        var tzMixed = LineAssembler.Assemble([Run("normal", 0, 700), Run("wide", 38, 700, tz: 120)]);
        Assert.True(Assert.Single(tzMixed).Mixed);
    }

    [Fact]
    public void ShortCellsOnSameBaseline_AreTableRows()
    {
        var runs = new List<TextRun> { Run("TAG", 0, 700), Run("P-101", 80, 700), Run("TAG2", 0, 688), Run("P-102", 80, 688) };
        var lines = LineAssembler.Assemble(runs);
        Assert.All(lines, l => Assert.True(l.TableRow));
        var single = LineAssembler.Assemble([Run("a long ordinary sentence that fills the column width of the page text", 0, 700, width: 300)]);
        Assert.False(Assert.Single(single).TableRow);
    }

    [Fact]
    public void Output_IsOrderedTopToBottomLeftToRight_AndDeterministic()
    {
        var runs = new List<TextRun> { Run("low", 0, 100), Run("high right", 200, 500), Run("high left", 0, 500) };
        var a = LineAssembler.Assemble(runs);
        Assert.Equal(["high left", "high right", "low"], a.Select(l => l.Text));
        var b = LineAssembler.Assemble([.. runs.AsEnumerable().Reverse()]);
        Assert.Equal(a.Select(l => l.Text), b.Select(l => l.Text));
    }

    [Fact]
    public void SampleLines_MatchTruthText_ForDocumentSamples()
    {
        foreach (var name in new[] { "doc-en-left", "doc-en-justified", "doc-ko-justified", "doc-ko-charwrap", "doc-2col", "doc-overlay-bold" })
        {
            var p = Read(_t, Sample(name));
            Assert.True(p.Lines.All(l => p.Match.ContainsKey(l)), $"{name}: 정답과 일치하지 않는 줄이 있음");
            Assert.Equal(p.Sample.Truth.Count, p.Match.Count);
        }
    }

    [Fact]
    public void JustifiedWordObjects_AreOneLinePerTruthLine()
    {
        var p = Read(_t, Sample("doc-en-justified"));
        // 낱말마다 글 객체인데 줄 하나로 조립된다
        Assert.True(p.Runs.Count > p.Lines.Length * 4);
        Assert.Equal(p.Sample.Truth.Count, p.Lines.Length);
    }

    [Fact]
    public void OverlaidSample_LineCountEqualsTruth_AndPdfiumReadsOnlyOneCopyText()
    {
        var p = Read(_t, Sample("doc-overlay-bold"));
        Assert.Equal(p.Sample.Truth.Count, p.Lines.Length);
        // 쪽에는 글 객체가 줄마다 2개(겹쳐 찍음)인데, PDFium 글 쪽(FPDFTextObj_GetText)은 겹친 글자를 한 번만 돌려줘
        // 두 번째 객체의 글이 빈 문자열이다. 그래서 PDFium으로 읽은 TextRun에는 한 벌만 들어오고 Duplicates는 비어 있다.
        // → ParagraphReader(Task 5)는 겹친 객체를 놓치지 않도록 빈 글 객체도 위치·경계로 짝지어야 한다(사용자 보고서에 기록).
        Assert.Equal(p.Lines.Length * 2, p.Data.TextObjects);
        Assert.All(p.Lines, l => Assert.Empty(l.Duplicates));
    }
}
