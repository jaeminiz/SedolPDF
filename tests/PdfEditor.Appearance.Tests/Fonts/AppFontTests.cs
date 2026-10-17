using System.Collections.Concurrent;
using PdfEditor.Appearance.Fonts;

namespace PdfEditor.Appearance.Tests.Fonts;

public class AppFontTests
{
    [Fact]
    public void AppFont_ConcurrentShaping_NoCrash()
    {
        using var font = AppFont.Load(FontTestSupport.FontPath());
        var errors = new ConcurrentBag<Exception>();
        var threads = Enumerable.Range(0, 4).Select(n => new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 200; i++)
                {
                    var shaped = font.Shape($"가나다 도면 검토 ABC {n}-{i}");
                    if (shaped.IsEmpty) throw new Exception("empty shaping result");
                    foreach (var g in shaped.Take(4))
                    {
                        font.GetCid(g.Gid);
                        font.Advance1000(g.Gid);
                        GlyphOutlines.Get(font, g.Gid);
                    }
                    font.GetGlyph('한');
                    if (i % 25 == 0) GC.Collect();
                }
            }
            catch (Exception e) { errors.Add(e); }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.Empty(errors);
    }

    [Fact]
    public void AppFont_Metrics_AreSane()
    {
        using var font = AppFont.Load(FontTestSupport.FontPath());
        Assert.Equal(1000, font.UnitsPerEm);
        Assert.InRange(font.Ascender1000, 700, 1300);
        Assert.InRange(font.Descender1000, -500, 0);
        int gid = font.GetGlyph('가')!.Value;
        Assert.InRange(font.Advance1000(gid), 900, 1100);
        Assert.Null(font.GetGlyph(0x10FFFE));
        Assert.True(font.GetCid(gid) > 0);
    }
}
