using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.TextEdit.Paragraphs;

namespace PdfEditor.TextEdit.Tests.Paragraphs;

/// <summary>M5 Task 4: 문단 다시 맞추기(줄바꿈·맞춤·최소 변경·넘침). 글 폭은 글자당 0.5 em(= 크기 10에서 5 pt)인 가짜 측정으로, PDF 없이 순수 계산만 시험한다.</summary>
public class ParagraphLayoutTests
{
    private sealed class FixedMeasure(double em = 0.5, string unsupported = "") : ITextMeasure
    {
        public float Advance(string text, float sizePt) => (float)(text.Length * em * sizePt);

        public bool CanRender(string text, out ImmutableArray<int> missing)
        {
            missing = [.. text.Where(c => unsupported.Contains(c)).Distinct().Select(c => (int)c)];
            return missing.IsEmpty;
        }
    }

    private static readonly FixedMeasure Orig = new();
    private const double Size = 10, Pitch = 12, ULeft = 100, VTop = 700;

    /// <summary>줄 글을 그대로 쓰는 문단 모델. 상자 폭 = cols × 5 pt. 글꼴 크기 10, 줄 간격 12.</summary>
    private static ParagraphModel Model(string[] lines, int cols, Alignment align = Alignment.Left, int angle = 0, double indent = 0,
        float maxBottom = float.NegativeInfinity)
    {
        var (ux, uy, vx, vy) = Geo.Axes(angle);
        PdfPointF P(double u, double v) => new((float)(u * ux + v * vx), (float)(u * uy + v * vy));
        double boxW = cols * 0.5 * Size;
        var tl = new List<TextLine>();
        for (int i = 0; i < lines.Length; i++)
        {
            double w = lines[i].Length * 0.5 * Size, v = VTop - i * Pitch;
            double u0 = align switch
            {
                Alignment.Center => ULeft + (boxW - w) / 2,
                Alignment.Right => ULeft + boxW - w,
                _ => ULeft + (i == 0 ? indent : 0),
            };
            var run = ParagraphTestData.Run(lines[i], u0, v, Size, "F1", angle, w);
            tl.Add(new TextLine
            {
                Runs = [run], Bounds = run.Bounds, Angle = angle, U0 = u0, U1 = u0 + w, Baseline = v, Size = Size,
                FontKey = "F1", Color = run.Color, Tz = 100, Mixed = false, Text = lines[i],
            });
        }
        double vBottom = VTop - (lines.Length - 1) * Pitch;
        var box = PdfRectF.FromPoints([P(ULeft, vBottom - 0.2 * Size), P(ULeft + boxW, vBottom - 0.2 * Size), P(ULeft, VTop + 0.8 * Size), P(ULeft + boxW, VTop + 0.8 * Size)]);
        var style = new ParagraphStyle("F1", (float)Size, new PdfColor(0, 0, 0), 100);
        return new ParagraphModel([.. tl], box, style, align, (float)indent, (float)Pitch, maxBottom, 1.0, null);
    }

    private static readonly string[] Four = ["alpha beta gamma", "delta eps", "zeta-long-word eta", "theta iota"];
    private static ParagraphModel FourLines(Alignment a = Alignment.Left) => Model(Four, 20, a);
    private const string FourText = "alpha beta gamma delta eps zeta-long-word eta theta iota";

    private static ParagraphLayoutResult Lay(ParagraphModel p, string text, ITextMeasure? app = null, LayoutOptions? opt = null) =>
        ParagraphLayout.Layout(p, text, Orig, app, opt);

    private static string[] Texts(ParagraphLayoutResult r) => r.Lines.Select(l => l.Text).ToArray();

    [Fact]
    public void OriginalText_JoinsLinesWithSpaces()
    {
        Assert.Equal(FourText, ParagraphLayout.OriginalText(FourLines()));
    }

    [Fact]
    public void OriginalText_CharacterMode_JoinsHangulWithoutSpace()
    {
        var p = Model(["참조하여 이", "동합니다"], 20);
        Assert.Equal("참조하여 이 동합니다", ParagraphLayout.OriginalText(p));
        Assert.Equal("참조하여 이동합니다", ParagraphLayout.OriginalText(p, new LayoutOptions { Korean = KoreanLineBreak.Character }));
    }

    [Fact]
    public void NoChange_NoEdit()
    {
        var p = FourLines();
        var r = Lay(p, FourText);
        Assert.True(r.FirstRewritten > r.LastRewritten);
        Assert.True(r.NoChange);
        Assert.False(r.DeleteAll);
        Assert.All(r.Lines, l => Assert.True(l.IsOriginal));
        Assert.Equal(Four, Texts(r));
        // 줄 끝 공백·\r\n 같은 겉모양만 다른 입력도 바뀐 것이 아니다
        Assert.True(Lay(p, FourText + "  \r\n").NoChange);
    }

    [Fact]
    public void SameLengthWordChange_OnlyThatLineRewritten()
    {
        var r = Lay(FourLines(), FourText.Replace("delta", "DELTA"));
        Assert.Equal(1, r.FirstRewritten);
        Assert.Equal(1, r.LastRewritten);
        Assert.Equal(Four.Length, r.Lines.Length);
        Assert.Equal(["alpha beta gamma", "DELTA eps", "zeta-long-word eta", "theta iota"], Texts(r));
        Assert.True(r.Lines[0].IsOriginal && r.Lines[2].IsOriginal && r.Lines[3].IsOriginal);
        Assert.False(r.Lines[1].IsOriginal);
        Assert.Equal(0, r.Lines[0].OriginalLineIndex);
        Assert.Equal(FontChoice.Original, r.Choice);
    }

    [Fact]
    public void LongerText_RewrapsFromChangedLine_SyncsBack()
    {
        // "betas more"를 넣으면 첫 줄이 넘쳐 gamma가 둘째 줄로 내려가고, 둘째 줄 끝은 원래 끝과 같아져 셋째 줄부터 원본이다.
        var r = Lay(FourLines(), FourText.Replace("beta", "betas more"));
        Assert.Equal(0, r.FirstRewritten);
        Assert.Equal(1, r.LastRewritten);
        Assert.True(r.Lines[2].IsOriginal && r.Lines[3].IsOriginal);
        Assert.Equal("alpha betas more", r.Lines[0].Text);
        Assert.Equal("gamma delta eps", r.Lines[1].Text);
    }

    [Fact]
    public void RewrapSyncsBackToOriginalBreaks()
    {
        // 둘째 줄 안에서 "eps"만 길어져도 줄바꿈이 그대로라 그 줄만 새로 쓴다. 앞 줄은 건드리지 않는다.
        var r = Lay(FourLines(), FourText.Replace("eps", "epsilon"));
        Assert.Equal(1, r.FirstRewritten);
        Assert.Equal(1, r.LastRewritten);
        Assert.Equal("delta epsilon", r.Lines[1].Text);
        Assert.True(r.Lines[0].IsOriginal);
        Assert.True(r.Lines[2].IsOriginal && r.Lines[3].IsOriginal);
        // 위치(기준선)도 원래 줄 격자 그대로
        for (int i = 0; i < 4; i++) Assert.Equal(700 - i * 12, r.Lines[i].BaselineOrigin.Y, 3);
    }

    [Fact]
    public void Shorter_FewerLines_BottomFreed()
    {
        var p = FourLines();
        var r = Lay(p, FourText.Replace("zeta-long-word ", ""));
        Assert.Equal(["alpha beta gamma", "delta eps eta theta", "iota"], Texts(r));
        Assert.Equal(3, r.Lines.Length);
        Assert.Equal(4, r.OriginalLineCount);
        Assert.Equal(1, r.FirstRewritten);
        Assert.Equal(2, r.LastRewritten);
        Assert.True(r.Lines[0].IsOriginal);
        Assert.False(r.NoChange);
    }

    [Fact]
    public void LongerText_AddsLinesBelow_NoOriginalAfterThem()
    {
        var r = Lay(FourLines(), FourText + " kappa lambda mu nu xi omicron pi");
        Assert.True(r.Lines.Length > 4);
        Assert.Equal(3, r.FirstRewritten);   // 마지막 줄의 끝 이어붙임이므로 마지막 줄부터(맨 앞이 아니라 줄 안에서 바뀜)
        Assert.Equal(r.Lines.Length - 1, r.LastRewritten);
        // 새 줄은 줄 간격 12씩 아래로
        for (int i = 0; i < r.Lines.Length; i++) Assert.Equal(700 - i * 12, r.Lines[i].BaselineOrigin.Y, 3);
    }

    [Fact]
    public void KoreanBreaksAtSpaces_ThenWithinWord()
    {
        var p = Model(["가나다 라마바사", "아자차카"], 8);
        var r = Lay(p, "가나다라 라마바사 아자차카");
        Assert.Equal(["가나다라", "라마바사", "아자차카"], Texts(r));   // 어절을 쪼개지 않는다
        var r2 = Lay(p, "가나다라마바사아자차카");                      // 공백 없는 긴 낱말만 글자 사이에서 끊는다
        Assert.Equal(["가나다라마바사아", "자차카"], Texts(r2));
    }

    [Fact]
    public void CharacterMode_BreaksBetweenHangulAnywhere()
    {
        var p = Model(["가나다라마바사아", "자차카타"], 8);
        var opt = new LayoutOptions { Korean = KoreanLineBreak.Character };
        Assert.Equal("가나다라마바사아자차카타", ParagraphLayout.OriginalText(p, opt));
        var r = Lay(p, "가나다라마바사아하자차카타", opt: opt);
        Assert.Equal(["가나다라마바사아", "하자차카타"], Texts(r));
    }

    [Fact]
    public void Kinsoku_NoClosingPunctuationAtLineStart()
    {
        var p = Model(["abcdefgh", "ij"], 8);
        var r = Lay(p, "abcdefgh.ij");   // 8글자 뒤가 마침표 → 마침표를 앞 줄에 두려 한 글자를 내려 보낸다
        Assert.Equal(["abcdefg", "h.ij"], Texts(r));
        Assert.DoesNotContain(r.Lines, l => l.Text.StartsWith('.'));
    }

    [Fact]
    public void Kinsoku_NoOpeningBracketAtLineEnd()
    {
        var p = Model(["abcdefgh", "ij"], 8);
        var r = Lay(p, "abcdefg(hij");
        Assert.Equal(["abcdefg", "(hij"], Texts(r));
    }

    [Fact]
    public void LongUnbrokenWord_BreaksByCharacter()
    {
        var p = Model(["abcdefgh", "ij"], 8);
        var r = Lay(p, "abcdefghijklmnopqrst");
        Assert.Equal(["abcdefgh", "ijklmnop", "qrst"], Texts(r));
    }

    [Fact]
    public void Justified_FillsWidth_ExceptLastLine()
    {
        var p = Model(["alpha beta gamma", "delta eps", "zeta-long-word eta", "theta iota"], 20, Alignment.Justified);
        var r = Lay(p, FourText.Replace("beta", "betas more"));
        var rewritten = r.Lines.Skip(r.FirstRewritten).Take(r.LastRewritten - r.FirstRewritten + 1).ToList();
        Assert.True(rewritten.Count >= 2);
        // 마지막으로 다시 쓴 줄 뒤에 원본 줄이 이어지므로 모두 "마지막 줄 아님" → 폭을 채운다
        foreach (var l in rewritten)
        {
            int spaces = l.Text.Count(c => c == ' ');
            Assert.True(spaces > 0);
            Assert.Equal(100.0, l.Width + l.JustifyExtra * spaces, 2);   // 상자 폭 20 × 5 pt
        }
        // 문단의 진짜 마지막 줄은 늘리지 않는다
        var r2 = Lay(p, FourText + " kappa lambda");
        Assert.Equal(0f, r2.Lines[^1].JustifyExtra);
        Assert.True(r2.Lines.Take(r2.Lines.Length - 1).Skip(r2.FirstRewritten).All(l => l.JustifyExtra > 0));
    }

    [Fact]
    public void Justified_NotAppliedToLeftAligned()
    {
        var r = Lay(FourLines(), FourText.Replace("beta", "betas more"));
        Assert.All(r.Lines, l => Assert.Equal(0f, l.JustifyExtra));
    }

    [Fact]
    public void Centered_And_Right_Origins()
    {
        var pc = Model(["alpha beta", "gamma", "delta eps"], 20, Alignment.Center);
        var rc = Lay(pc, "alpha beta gamma delta eps zeta");
        foreach (var l in rc.Lines.Skip(rc.FirstRewritten).Take(rc.LastRewritten - rc.FirstRewritten + 1))
            Assert.Equal(ULeft + 50 - l.Width / 2.0, l.BaselineOrigin.X, 2);
        var pr = Model(["alpha beta", "gamma", "delta eps"], 20, Alignment.Right);
        var rr = Lay(pr, "alpha beta gamma delta eps zeta");
        foreach (var l in rr.Lines.Skip(rr.FirstRewritten).Take(rr.LastRewritten - rr.FirstRewritten + 1))
            Assert.Equal(ULeft + 100 - l.Width, l.BaselineOrigin.X, 2);
    }

    [Fact]
    public void FirstLineIndent_AppliedToRewrittenFirstLine()
    {
        var p = Model(["    alpha beta"[4..], "gamma delta", "eps"], 20, indent: 20);
        var r = Lay(p, "ALPHA beta gamma delta eps");
        Assert.Equal(0, r.FirstRewritten);
        Assert.Equal(ULeft + 20, r.Lines[0].BaselineOrigin.X, 2);
    }

    [Fact]
    public void HardNewline_ForcesBreak()
    {
        var r = Lay(FourLines(), "aaa bbb\nccc ddd");
        Assert.Equal(["aaa bbb", "ccc ddd"], Texts(r));
        var p = Model(["alpha beta", "gamma"], 20, Alignment.Justified);
        var rj = Lay(p, "alpha beta\ngamma delta");
        Assert.Equal(0f, rj.Lines[0].JustifyExtra);   // 강제 줄바꿈 앞 줄은 마지막 줄 취급
    }

    [Fact]
    public void EmptyText_ReturnsDeleteAll()
    {
        var r = Lay(FourLines(), " \n ");
        Assert.True(r.DeleteAll);
        Assert.Empty(r.Lines);
        Assert.Equal(4, r.OriginalLineCount);
        Assert.False(r.NoChange);
    }

    [Fact]
    public void OverlapAllowed_FlaggedNotBlocked()
    {
        // 마지막 줄(기준선 664) 바로 아래 장애물 위쪽 끝이 658 → 줄이 하나 늘면(기준선 652) 겹친다. 막지 않고 표시만.
        var p = Model(Four, 20, maxBottom: 658f);
        var same = Lay(p, FourText.Replace("delta", "DELTA"));
        Assert.False(same.Overlap);
        var r = Lay(p, FourText + " kappa lambda mu nu xi");
        Assert.True(r.Overlap);
        Assert.False(r.Overflow);
        Assert.True(r.Lines.Length > 4);
    }

    [Fact]
    public void PageBottom_LimitsMaxLines()
    {
        // 첫 기준선 700, 줄 간격 12, 아래 여백 2.5 → 쪽 아래 y = 650이면 기준선 ≥ 652.5인 줄만: 700,688,676,664 → 4줄
        var opt = new LayoutOptions { PageBounds = new PdfRectF(0, 650, 600, 800) };
        var r = Lay(FourLines(), FourText.Replace("delta", "DELTA"), opt: opt);
        Assert.Equal(4, r.MaxLines);
        Assert.False(r.Overflow);
        var none = Lay(FourLines(), FourText.Replace("delta", "DELTA"));
        Assert.Equal(int.MaxValue, none.MaxLines);
    }

    [Fact]
    public void Overflow_FlaggedNotTruncated()
    {
        var opt = new LayoutOptions { PageBounds = new PdfRectF(0, 650, 600, 800) };
        var r = Lay(FourLines(), FourText + " kappa lambda mu nu xi omicron pi rho sigma", opt: opt);
        Assert.True(r.Overflow);
        Assert.True(r.Lines.Length > r.MaxLines);
        Assert.Equal(4, r.MaxLines);
        Assert.Contains("sigma", Texts(r)[^1]);   // 잘라 내지 않는다
    }

    [Fact]
    public void Rotated90_Paragraph_Rewraps()
    {
        var p0 = Model(Four, 20, angle: 0);
        var p90 = Model(Four, 20, angle: 90);
        string text = FourText.Replace("beta", "betas more");
        var r0 = Lay(p0, text);
        var r90 = Lay(p90, text);
        Assert.Equal(Texts(r0), Texts(r90));
        Assert.Equal(r0.FirstRewritten, r90.FirstRewritten);
        Assert.Equal(r0.LastRewritten, r90.LastRewritten);
        Assert.Equal(90, r90.Angle);
        // 90°: 글이 위로 흐른다 → 쪽 y = u, 쪽 x = -v. 줄이 아래(로컬 v 감소)로 쌓이면 쪽 x가 커진다.
        for (int i = 0; i < r90.Lines.Length; i++)
        {
            Assert.Equal(-(700 - i * 12), r90.Lines[i].BaselineOrigin.X, 2);
            Assert.Equal(r0.Lines[i].BaselineOrigin.X, r90.Lines[i].BaselineOrigin.Y, 2);   // 로컬 u는 같다
        }
    }

    [Fact]
    public void UnsupportedChar_FallsToAppFont_AllRewrittenLines()
    {
        var orig = new FixedMeasure(unsupported: "Z");
        var app = new FixedMeasure(em: 0.45);   // 폭이 달라 줄바꿈이 새로 맞춰진다
        var p = FourLines();
        var r = ParagraphLayout.Layout(p, FourText.Replace("beta", "betaZ more"), orig, app);
        Assert.Equal(FontChoice.App, r.Choice);
        Assert.Empty(r.MissingCodepoints);
        Assert.Contains("Z", r.RewrittenText);
        for (int i = r.FirstRewritten; i <= r.LastRewritten; i++) Assert.False(r.Lines[i].IsOriginal);
        for (int i = 0; i < r.FirstRewritten; i++) Assert.True(r.Lines[i].IsOriginal);
        for (int i = r.LastRewritten + 1; i < r.Lines.Length; i++) Assert.True(r.Lines[i].IsOriginal);

        // 원래 글꼴로 되는 글이면 원래 글꼴을 쓴다
        var ok = ParagraphLayout.Layout(p, FourText.Replace("beta", "betas more"), orig, app);
        Assert.Equal(FontChoice.Original, ok.Choice);
    }

    [Fact]
    public void MissingInBothFonts_ReportsCodepoints()
    {
        var orig = new FixedMeasure(unsupported: "Z");
        var app = new FixedMeasure(unsupported: "Z");
        var r = ParagraphLayout.Layout(FourLines(), FourText.Replace("beta", "betaZ"), orig, app);
        Assert.Equal(FontChoice.App, r.Choice);
        Assert.Equal([(int)'Z'], r.MissingCodepoints.ToArray());
        var rNoApp = ParagraphLayout.Layout(FourLines(), FourText.Replace("beta", "betaZ"), orig, null);
        Assert.Equal([(int)'Z'], rNoApp.MissingCodepoints.ToArray());
    }

    [Fact]
    public void NoOriginalFont_UsesAppDirectly()
    {
        var r = ParagraphLayout.Layout(FourLines(), FourText.Replace("delta", "DELTA"), null, new FixedMeasure());
        Assert.Equal(FontChoice.App, r.Choice);
        Assert.Equal(1, r.FirstRewritten);
    }

    [Fact]
    public void Layout_IsDeterministic()
    {
        var p = FourLines(Alignment.Justified);
        var a = Lay(p, FourText.Replace("beta", "betas more"));
        var b = Lay(p, FourText.Replace("beta", "betas more"));
        Assert.Equal(Texts(a), Texts(b));
        Assert.Equal(a.Lines.Select(l => l.BaselineOrigin), b.Lines.Select(l => l.BaselineOrigin));
    }

    [Fact]
    public void ChangeAtLineStart_LetsPreviousLineAbsorb()
    {
        // 셋째 줄 첫 낱말을 지우면 둘째 줄이 그 다음 낱말을 받을 수 있다 → 둘째 줄부터 다시
        var p = FourLines();
        var r = Lay(p, FourText.Replace("zeta-long-word ", ""));
        Assert.Equal(1, r.FirstRewritten);
    }

    // ---- M5 Task 8: 사용자가 끌어 정한 열 폭(ColumnWidthOverride, C13) ----

    [Fact]
    public void ColumnWidthOverride_Narrower_RewrapsAllLines_EvenWithSameText()
    {
        var p = FourLines() with { ColumnWidthOverride = 8 * 5 };   // 8자(40 pt) 폭
        var r = Lay(p, FourText);
        Assert.False(r.NoChange);
        Assert.Equal(0, r.FirstRewritten);
        Assert.True(r.Lines.Length > Four.Length);
        Assert.All(r.Lines, l => Assert.True(l.Width <= 40.5, l.Text));
        Assert.All(r.Lines, l => Assert.False(l.IsOriginal));
    }

    [Fact]
    public void ColumnWidthOverride_Wider_ShortensParagraph()
    {
        var p = FourLines() with { ColumnWidthOverride = 60 * 5 };
        var r = Lay(p, FourText);
        Assert.False(r.NoChange);
        Assert.True(r.Lines.Length < Four.Length);
        Assert.Equal(FourText, string.Join(" ", r.Lines.Select(l => l.Text)));
    }

    [Fact]
    public void NoColumnWidthOverride_SameText_StillNoChange()
    {
        Assert.True(Lay(FourLines(), FourText).NoChange);
    }
}
