using Pdf.Native;
using PdfEditor.Appearance.Fonts;

namespace PdfEditor.Appearance.Tests.Fonts;

public class GlyphOutlinesTests
{
    [Fact]
    public void GlyphOutlines_Ga_NonEmptyClosedPaths()
    {
        using var font = AppFont.Load(FontTestSupport.FontPath());
        int gid = font.GetGlyph('가') ?? throw new Exception("no glyph");
        var path = GlyphOutlines.Get(font, gid);
        Assert.False(path.IsEmpty);
        int moves = path.Count(s => s is MoveTo), closes = path.Count(s => s is ClosePath);
        Assert.True(moves >= 2, "has several contours");
        Assert.Equal(moves, closes);
        Assert.IsType<MoveTo>(path[0]);
        Assert.IsType<ClosePath>(path[^1]);
        // em 단위: 좌표가 대략 -0.3 ~ 1.3 안
        foreach (var s in path)
            foreach (var p in Points(s))
                Assert.InRange(p.X, -0.3f, 1.3f);
        // 캐시: 같은 배열을 돌려준다
        Assert.Equal(path, GlyphOutlines.Get(font, gid));
    }

    [Fact]
    public void GlyphOutlines_Space_Empty()
    {
        using var font = AppFont.Load(FontTestSupport.FontPath());
        Assert.True(GlyphOutlines.Get(font, font.GetGlyph(' ')!.Value).IsEmpty);
    }

    private static IEnumerable<PdfPointF> Points(PathSeg s) => s switch
    {
        MoveTo m => [m.P],
        LineTo l => [l.P],
        CubicTo c => [c.C1, c.C2, c.P],
        _ => [],
    };
}
