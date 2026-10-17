using System.Text;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations.Patching;

namespace PdfEditor.Appearance.Tests.Fonts;

internal static class FontTestSupport
{
    public static string FontPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var f = Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf");
            if (File.Exists(f)) return f;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("assets/fonts/NotoSansKR-Regular.otf");
    }

    /// <summary>줄 하나를 전체 글꼴로 셰이핑해 FontUse(코드포인트·CID·폭·글자)와 CID 16진 문자열을 만든다.</summary>
    public static (FontUse Use, string CidHex) UseFor(AppFont font, string text)
    {
        var cps = text.EnumerateRunes().Select(r => r.Value).Distinct().ToArray();
        var glyphs = new List<GlyphUse>();
        var hex = new StringBuilder();
        foreach (var g in font.Shape(text))
        {
            int cid = font.GetCid(g.Gid);
            string t = g.Cluster < text.Length ? text[g.Cluster].ToString() : "";
            glyphs.Add(new GlyphUse(cid, g.Advance1000, t));
            hex.Append(cid.ToString("X4"));
        }
        return (new FontUse([.. cps], [.. glyphs]), hex.ToString());
    }
}
