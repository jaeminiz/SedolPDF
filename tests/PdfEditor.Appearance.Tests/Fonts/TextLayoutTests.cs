using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance.Tests.Fonts;

public class TextLayoutTests : IDisposable
{
    private readonly AppFont _font = AppFont.Load(FontTestSupport.FontPath());
    private static readonly PdfPointF Origin = new(10, 500);
    private const float Size = 12f;

    public void Dispose() => _font.Dispose();

    private static string LineText(IEnumerable<PositionedGlyph> line) => string.Concat(line.Select(g => g.Text));

    private float WidthOf(string s)
        => TextLayout.Layout(_font, s, Size, float.PositiveInfinity, TextAlign.Left, Origin).Bounds.Width;

    [Fact]
    public void Layout_WrapsAtSpaceWithinBox()
    {
        float box = Math.Max(WidthOf("hello"), WidthOf("world")) + 1f;   // each word fits alone, two do not
        var r = TextLayout.Layout(_font, "hello world foo", Size, box, TextAlign.Left, Origin);
        Assert.Equal(["hello", "world", "foo"], r.Lines.Select(l => LineText(l)).ToArray());   // trailing spaces are not emitted
        Assert.All(r.Lines, l => Assert.True(l[^1].Origin.X + l[^1].Advance - Origin.X <= box + 0.01f));
        Assert.Empty(r.MissingCodepoints);
    }

    [Fact]
    public void Layout_KoreanBreaksBetweenSyllables()
    {
        const string text = "가나다라마바사아자차";
        float box = WidthOf("가나다") + 1f;
        var r = TextLayout.Layout(_font, text, Size, box, TextAlign.Left, Origin);
        Assert.Equal(["가나다", "라마바", "사아자", "차"], r.Lines.Select(l => LineText(l)).ToArray());
        // 줄 높이 1.2 x 크기
        Assert.Equal(Size * 1.2f, r.Lines[0][0].Origin.Y - r.Lines[1][0].Origin.Y, 3);
    }

    [Fact]
    public void Layout_LongWordBreaksAnywhere()
    {
        const string word = "Supercalifragilisticexpialidocious";
        float box = 50f;
        var r = TextLayout.Layout(_font, word, Size, box, TextAlign.Left, Origin);
        Assert.True(r.Lines.Length > 1);
        Assert.Equal(word, string.Concat(r.Lines.Select(l => LineText(l))));
        foreach (var l in r.Lines)
        {
            Assert.NotEmpty(l);
            Assert.True(l[^1].Origin.X + l[^1].Advance - Origin.X <= box + 0.01f);
        }
        // 한 글자도 안 들어가는 좁은 상자에서도 끝나고 글자당 한 줄
        var narrow = TextLayout.Layout(_font, "abc", Size, 1f, TextAlign.Left, Origin);
        Assert.Equal(["a", "b", "c"], narrow.Lines.Select(l => LineText(l)).ToArray());
    }

    [Fact]
    public void Layout_MixedScriptsAndBlankLines()
    {
        var r = TextLayout.Layout(_font, "가A\n\n😀b", Size, float.PositiveInfinity, TextAlign.Left, Origin);
        Assert.Equal(3, r.Lines.Length);
        Assert.Equal("가A", LineText(r.Lines[0]));
        Assert.Empty(r.Lines[1]);
        Assert.Equal("b", LineText(r.Lines[2]));                 // 이모지는 글꼴에 없어 빠지고 알림
        Assert.Contains(0x1F600, r.MissingCodepoints);
        float lh = Size * 1.2f;
        Assert.Equal(2 * lh, r.Lines[0][0].Origin.Y - r.Lines[2][0].Origin.Y, 3);   // 빈 줄도 높이를 차지
        Assert.Equal(3 * lh, r.Bounds.Height, 3);
        Assert.Equal(Origin.Y, r.Bounds.Top, 3);
    }

    [Fact]
    public void Layout_MissingGlyph_ReportedNotSilent()
    {
        var r = TextLayout.Layout(_font, "A\U0001F600B\U0001F601\U0001F600", Size, float.PositiveInfinity, TextAlign.Left, Origin);
        Assert.Equal([0x1F600, 0x1F601], r.MissingCodepoints.ToArray());
        Assert.Equal("AB", LineText(r.Lines[0]));
        Assert.DoesNotContain(r.Lines[0], g => g.Gid == 0);       // .notdef를 그리지 않는다
        Assert.All(r.Lines[0], g => Assert.True(g.Cid > 0));
    }

    [Fact]
    public void Layout_AlignCenterRight()
    {
        float box = 200f;
        float w = WidthOf("Hi");
        var left = TextLayout.Layout(_font, "Hi", Size, box, TextAlign.Left, Origin);
        var center = TextLayout.Layout(_font, "Hi", Size, box, TextAlign.Center, Origin);
        var right = TextLayout.Layout(_font, "Hi", Size, box, TextAlign.Right, Origin);
        Assert.Equal(Origin.X, left.Lines[0][0].Origin.X, 3);
        Assert.Equal(Origin.X + (box - w) / 2f, center.Lines[0][0].Origin.X, 2);
        Assert.Equal(Origin.X + box - w, right.Lines[0][0].Origin.X, 2);
        Assert.Equal(Origin.X + box, right.Bounds.Right, 2);
    }

    [Fact]
    public void Layout_GlyphsCarryCidAndNominalAdvance()
    {
        var r = TextLayout.Layout(_font, "가", Size, float.PositiveInfinity, TextAlign.Left, Origin);
        var g = r.Lines[0][0];
        Assert.Equal(_font.GetGlyph('가'), g.Gid);
        Assert.Equal(_font.GetCid(g.Gid), g.Cid);
        Assert.Equal(_font.Advance1000(g.Gid) * Size / 1000f, g.Advance, 3);
        Assert.Equal("가", g.Text);
        Assert.Equal(Origin.X, g.Origin.X, 3);
    }

    [Fact]
    public void Layout_ConcurrentCalls_AreSafe()
    {
        var results = new string[8];
        Parallel.For(0, 8, i =>
        {
            var r = TextLayout.Layout(_font, "가나다 abc 😀", Size, 60f, TextAlign.Left, Origin);
            results[i] = string.Join("|", r.Lines.Select(l => LineText(l)));
        });
        Assert.Single(results.Distinct());
    }
}
