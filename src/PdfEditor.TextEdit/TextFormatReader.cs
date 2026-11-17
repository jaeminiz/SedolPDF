using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;

namespace PdfEditor.TextEdit;

/// <summary>고른 글 객체에서 읽은 글자 모양. 굵게·기울임·명조는 글꼴 이름·플래그 등에서 추정한 값이다.</summary>
public sealed record TextFormatInfo(string FontName, float SizePt, PdfColor Color, bool Bold, bool Italic, bool Serif, bool IsOriginalFont)
{
    /// <summary>앱이 설치된 글꼴로 다시 쓴 줄인가(0.12.0). 이때 FontName은 그 글꼴의 보이는 이름.</summary>
    public bool SystemFont { get; init; }
}

/// <summary>
/// 글꼴 모양 바꾸기 요청. null이면 그대로 둔다. 하나라도 있으면 줄을 다시 쓴다.
/// FontName(0.12.0): 설치된 글꼴 이름이면 그 글꼴로, Serif만 주고 FontName이 null이면 앱 글꼴(고딕/명조)로 돌아간다.
/// </summary>
public sealed record TextFormatOverride(bool? Bold = null, bool? Italic = null, bool? Serif = null, string? FontName = null)
{
    public bool IsEmpty => Bold is null && Italic is null && Serif is null && FontName is null;
}

/// <summary>쪽 글 객체의 글꼴 이름·크기·색·굵게·기울임·명조를 읽는다(PDFium 전용 스레드).</summary>
public static class TextFormatReader
{
    public const string AppSansName = "Noto Sans KR";
    public const string AppSerifName = "Noto Serif KR";

    /// <param name="overlappedCopy">같은 자리에 같은 글을 겹쳐 찍은 객체가 있으면 true(가짜 굵게로 본다).</param>
    public static TextFormatInfo? Read(DocumentSession s, PageId page, ObjectRef target, bool overlappedCopy = false)
    {
        s.Thread.AssertOnThread();
        if (s.IndexOf(page) < 0) return null;
        if (target.IsInserted && s.Content.For(page).OfType<InsertAppTextEdit>().FirstOrDefault(a => a.Id == target.InsertedId) is { } app)
        {
            var l = app.Line;
            // 설치된 글꼴의 진짜 굵은 얼굴이면 BoldStrokePt가 0이라 굵게를 글꼴 이름(얼굴)으로 알 수 없다: FontUse 키의 얼굴은 앱이 굵게 표시를 따로 둔다.
            return new TextFormatInfo(l.FontName ?? (l.Serif ? AppSerifName : AppSansName), l.SizePt, l.Color, l.BoldStrokePt > 0 || l.RealBold, l.SyntheticItalic || l.RealItalic, l.Serif, false)
                { SystemFont = l.FontName is not null };
        }
        if (s.GetReplay(page).Resolve(target) is not { Type: PdfPageObjectType.Text } o) return null;
        var font = o.Font;
        var (family, bold, italic) = AppFontCatalog.Classify(new PdfFontInfo(font?.BaseName));
        bold |= o.TextRenderMode == 2 || overlappedCopy;
        var m = o.Matrix;
        float scale = MathF.Sqrt(MathF.Abs(m.A * m.D - m.B * m.C));   // 객체를 크게/작게 바꾸면 글자 크기는 그대로이고 행렬만 커진다: 보이는 크기 = 글자 크기 x 행렬 배율
        if (scale < 0.001f) scale = 1f;
        var fc = o.FillColor;
        var color = fc is { } c ? new PdfColor(c.R, c.G, c.B) : new PdfColor(0, 0, 0);
        return new TextFormatInfo(StripSubsetTag(font?.BaseName ?? ""), o.FontSize * scale, color, bold, italic, family == AppFontFamily.Serif, true);
    }

    private static string StripSubsetTag(string n) => n.Length > 7 && n[6] == '+' && n.Take(6).All(char.IsAsciiLetterUpper) ? n[7..] : n;
}
