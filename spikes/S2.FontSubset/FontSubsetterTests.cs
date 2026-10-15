using HarfBuzzSharp;

namespace S2.FontSubset;

public class FontSubsetterTests
{
    private static byte[] LoadFont()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var f = Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf");
            if (File.Exists(f)) return File.ReadAllBytes(f);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("assets/fonts/NotoSansKR-Regular.otf");
    }

    private static int[] Cps(string s) => s.EnumerateRunes().Select(r => r.Value).Distinct().ToArray();


    private static bool HasGlyph(byte[] font, int cp)
    {
        using var blob = FontSubsetter.CreateBlob(font);
        using var face = new Face(blob, 0);
        using var f = new Font(face);
        return f.TryGetGlyph((uint)cp, out _);
    }

    [Fact]
    public void Subset_KoreanSample_ContainsOnlyRequestedGlyphs()
    {
        var sub = FontSubsetter.Subset(LoadFont(), Cps("가나다 도면 ABC 123"));
        foreach (var cp in Cps("가나다도면ABC123"))
            Assert.True(HasGlyph(sub, cp), $"U+{cp:X4} 누락");
        Assert.False(HasGlyph(sub, '힣'));
        Assert.False(HasGlyph(sub, 'Z'));
    }

    [Fact]
    public void Subset_KoreanSample_IsUnder5PercentOfOriginal()
    {
        var orig = LoadFont();
        var sub = FontSubsetter.Subset(orig, Cps("가나다 도면 ABC 123"));
        Assert.True(sub.Length < orig.Length * 0.05, $"{sub.Length} / {orig.Length}");
    }

    [Fact]
    public void Subset_EmptySet_ReturnsValidFontWithNotdefOnly()
    {
        var sub = FontSubsetter.Subset(LoadFont(), Array.Empty<int>());
        using var blob = FontSubsetter.CreateBlob(sub);
        using var face = new Face(blob, 0);
        Assert.Equal(1, face.GlyphCount);
        Assert.False(HasGlyph(sub, 'A'));
    }
}
