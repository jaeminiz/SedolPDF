using System.Collections.Immutable;
using PdfEditor.Appearance.Fonts;
using PdfEditor.ContentStream.Fonts;

namespace PdfEditor.TextEdit.Paragraphs;

/// <summary>글 폭 측정(순수 계산). 자간(Tc)·가로 비율(Tz)은 부르는 쪽(<see cref="ParagraphLayout"/>)이 얹는다.</summary>
public interface ITextMeasure
{
    /// <summary>글자 크기 <paramref name="sizePt"/>로 쓴 <paramref name="text"/>의 글 방향 이동량(pt, Tc·Tz 제외).</summary>
    float Advance(string text, float sizePt);

    /// <summary>모든 글자를 이 글꼴로 그릴 수 있나. 못 그리는 글자는 코드포인트로 <paramref name="missing"/>에 담는다(공백·줄바꿈 글자는 제외).</summary>
    bool CanRender(string text, out ImmutableArray<int> missing);
}

/// <summary>원래 글꼴 측정: 코드북의 글꼴 폭표(<c>/Widths</c>, <c>/W</c>). 코드북에 없는 글자는 그릴 수 없다.</summary>
public sealed class CodebookMeasure(FontCodebook book) : ITextMeasure
{
    public FontCodebook Book { get; } = book;

    public float Advance(string text, float sizePt)
    {
        float sum = 0;
        foreach (char ch in text) sum += Book.Width1000(ch);
        return sum * sizePt / 1000f;
    }

    public bool CanRender(string text, out ImmutableArray<int> missing)
    {
        var avail = Book.Available.ToHashSet();
        var list = ImmutableArray.CreateBuilder<int>();
        foreach (char ch in text)
        {
            if (ch == '\n' || ch == '\r') continue;
            if (!avail.Contains(ch) && !list.Contains(ch)) list.Add(ch);
        }
        missing = list.ToImmutable();
        return missing.IsEmpty;
    }
}

/// <summary>앱 글꼴(Noto) 측정: HarfBuzz 배치(<see cref="AppFont.Shape"/>)의 이동량.</summary>
public sealed class AppFontMeasure(AppFont font) : ITextMeasure
{
    public float Advance(string text, float sizePt)
    {
        string line = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        if (line.Length == 0) return 0;
        float sum = 0;
        foreach (var g in font.Shape(line)) sum += g.Advance1000;
        return sum * sizePt / 1000f;
    }

    public bool CanRender(string text, out ImmutableArray<int> missing)
    {
        var list = ImmutableArray.CreateBuilder<int>();
        for (int i = 0; i < text.Length; i++)
        {
            int cp = char.ConvertToUtf32(text, i);
            if (char.IsHighSurrogate(text[i])) i++;
            if (cp is '\n' or '\r') continue;
            if (font.GetGlyph(cp) is null && !list.Contains(cp)) list.Add(cp);
        }
        missing = list.ToImmutable();
        return missing.IsEmpty;
    }
}
