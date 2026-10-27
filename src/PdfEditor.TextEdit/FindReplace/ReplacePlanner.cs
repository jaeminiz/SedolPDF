using System.Collections.Immutable;
using PdfEditor.ContentStream.Fonts;
using PdfEditor.Document.Content;

namespace PdfEditor.TextEdit.FindReplace;

public enum WordRefusal
{
    None,
    /// <summary>폼 XObject 안의 글 — 바꿀 수 없다(V15).</summary>
    InForm,
    /// <summary>`'`·`"` 연산자로 보인 글 — 지원하지 않는다.</summary>
    QuoteOperator,
    /// <summary>글자를 가두는 렌더 모드(클립)라 바꿀 수 없다.</summary>
    ClipText,
    /// <summary>글꼴 크기를 알 수 없다.</summary>
    NoFontSize,
    /// <summary>이미 바꿨거나 지운 글이다.</summary>
    AlreadyEdited,
    /// <summary>암호 문서 — 바꾸기는 끈다(M4x U-B).</summary>
    Encrypted,
    /// <summary>파일과 지금 문서의 쪽 구성이 달라 글 객체 번호가 맞지 않는다.</summary>
    PageMismatch,
    /// <summary>글 객체를 찾지 못했다.</summary>
    ObjectNotFound,
    /// <summary>원래 글꼴로도 앱 글꼴로도 그릴 수 없는 글자가 있다.</summary>
    MissingGlyphs,
    /// <summary>앱 글꼴이 필요한데 없다.</summary>
    NoAppFont,
}

/// <summary>새 글을 어떤 글꼴로 쓰는가.</summary>
public enum ReplaceFontKind
{
    /// <summary>원래 글꼴(코드북: 같은 글꼴 사전에서 보인 글 코드).</summary>
    Original,
    /// <summary>비임베드 단순 글꼴의 표준 인코딩 글자(WinAnsi 직접 쓰기, 0.4.0 확정).</summary>
    WinAnsiDirect,
    /// <summary>앱 글꼴(고딕/명조) — 그 낱말만. 미리보기에서 알린다(N1).</summary>
    App,
}

/// <summary>낱말 하나의 바꾸기 계획(PDFium을 모르는 순수 계산). Cuts는 글 연산(Op)별로 나눈 빼낼 글 코드 범위, 새 글은 첫 글 연산에 붙는다.</summary>
public sealed record WordReplacement(TextMatch Match, string NewText, WordRefusal Refusal, ReplaceFontKind Font, byte[]? NewBytes,
    ImmutableArray<(int Op, ImmutableArray<TextCutRef> Cuts)> OpCuts, TextMatrix Tm, double NewLengthPt, bool Overlap,
    FontCodebook? Book)
{
    public bool Ok => Refusal == WordRefusal.None;
    public int FirstOp => OpCuts.IsDefaultOrEmpty ? -1 : OpCuts[0].Op;
}

/// <summary>
/// 찾은 낱말 → 바꾸기 계획(0.4.0 D8·D12). 글꼴 결정: ① 쪽의 보인 글 코드 코드북 ② 같은 글꼴 사전을 쓰는 문서 전체의 코드북(documentBook) ③ 비임베드 단순 글꼴의 ASCII 직접 쓰기 ④ 앱 글꼴.
/// 길이가 달라져도 뒤따르는 글은 건드리지 않는다 — 새 글이 같은 줄의 뒤 글을 덮으면 Overlap으로 알린다.
/// </summary>
public static class ReplacePlanner
{
    public static WordReplacement Plan(TextMatch m, string newText, Func<FontInfo, FontCodebook?>? documentBook = null)
    {
        WordReplacement Refuse(WordRefusal r) => new(m, newText, r, ReplaceFontKind.App, null, [], default, 0, false, null);
        if (m.Glyphs.Count == 0) return Refuse(WordRefusal.ObjectNotFound);
        if (m.InForm) return Refuse(WordRefusal.InForm);
        if (m.HasQuoteOp) return Refuse(WordRefusal.QuoteOperator);
        var first = m.Glyphs[0];
        if (m.Glyphs.Any(g => g.Tr >= 4)) return Refuse(WordRefusal.ClipText);
        if (first.FontSize <= 0 || first.Font is null) return Refuse(WordRefusal.NoFontSize);

        // 글 연산별로 나눈 빼낼 범위(첫 글 연산 먼저, 읽기 순서)
        var opCuts = ImmutableArray.CreateBuilder<(int, ImmutableArray<TextCutRef>)>();
        foreach (var grp in m.Glyphs.GroupBy(g => g.Op))
        {
            var cuts = ImmutableArray.CreateBuilder<TextCutRef>();
            foreach (var span in TextMatcher.SpansOf([.. grp]))
            {
                double adv = grp.Where(g => g.Str == span.Str && g.Code >= span.FromCode && g.Code < span.FromCode + span.CodeCount).Sum(g => g.AdvText);
                cuts.Add(new TextCutRef(span.Str, span.FromCode, span.CodeCount, adv));
            }
            opCuts.Add((grp.Key, cuts.ToImmutable()));
        }
        // 첫 글자가 있는 글 연산이 맨 앞에 오도록(읽기 순서 = 글 연산 순서가 아닐 수 있다)
        int firstOp = first.Op;
        var ordered = opCuts.ToImmutable().OrderBy(x => x.Item1 == firstOp ? 0 : 1).ThenBy(x => x.Item1).ToImmutableArray();

        // 글꼴 결정
        var font = first.Font;
        byte[]? bytes = null; ReplaceFontKind kind = ReplaceFontKind.App; double lengthPt = 0; FontCodebook? used = null;
        double sc = Math.Sqrt(first.Place.A * first.Place.A + first.Place.B * first.Place.B);
        double Len(IEnumerable<float> widths, int count) => (widths.Sum() / 1000.0 * first.FontSize + first.Tc * count) * first.Tz / 100.0 * sc;
        if (newText.Length == 0) kind = ReplaceFontKind.Original;   // 낱말을 지우기만 한다(새 글 없음)
        else if (font.Book is { } pageBook && pageBook.TryEncode(newText, out var e1)) { bytes = e1.Bytes; kind = ReplaceFontKind.Original; lengthPt = Len(e1.Widths, newText.Length); used = pageBook; }
        else if (documentBook?.Invoke(font) is { } docBook && docBook.TryEncode(newText, out var e2)) { bytes = e2.Bytes; kind = ReplaceFontKind.Original; lengthPt = Len(e2.Widths, newText.Length); used = docBook; }
        else if (!font.Type0 && !font.Type3 && !font.Embedded && SimpleEncoding(font) && newText.All(c => c is >= ' ' and <= '~'))
        {
            bytes = System.Text.Encoding.Latin1.GetBytes(newText);
            kind = ReplaceFontKind.WinAnsiDirect;
            lengthPt = Len(newText.Select(c => font.Widths is not null && c - font.First >= 0 && c - font.First < font.Widths.Length ? (float)font.Widths[c - font.First] : (float)font.Missing), newText.Length);
        }
        // 앱 글꼴 줄의 길이는 서비스가 만든 뒤 알린다(여기서는 0).
        return new WordReplacement(m, newText, WordRefusal.None, kind, bytes, ordered, new TextMatrix(first.Tm.A, first.Tm.B, first.Tm.C, first.Tm.D, first.Tm.E, first.Tm.F),
            lengthPt, kind != ReplaceFontKind.App && OverlapsFollowing(m, lengthPt), used);
    }

    /// <summary>비임베드 단순 글꼴이 표준 인코딩(없음·WinAnsi·MacRoman·Standard — Differences 없음)인가.</summary>
    private static bool SimpleEncoding(FontInfo f)
    {
        var enc = f.Dict.Elements["/Encoding"];
        if (enc is null) return true;
        if (enc is PdfSharp.Pdf.PdfName n) return n.Value is "/WinAnsiEncoding" or "/MacRomanEncoding" or "/StandardEncoding";
        return false;
    }

    /// <summary>새 글(lengthPt)이 같은 줄의 뒤 글을 덮는가 — 길이가 늘어난 경우만.</summary>
    public static bool OverlapsFollowing(TextMatch m, double lengthPt)
    {
        if (m.Source is not { } src || m.LineIndex < 0 || m.LineIndex >= src.Lines.Count) return false;
        var first = m.Glyphs[0]; var last = m.Glyphs[^1];
        double th = Math.Atan2(first.Place.B, first.Place.A), dx = Math.Cos(th), dy = Math.Sin(th);
        double startU = first.X0 * dx + first.Y0 * dy, oldEndU = last.X1 * dx + last.Y1 * dy, newEndU = startU + lengthPt;
        if (newEndU <= oldEndU + 0.01) return false;
        return src.Lines[m.LineIndex].Glyphs.Any(g => !g.Virtual && !m.Glyphs.Contains(g)
            && g.X0 * dx + g.Y0 * dy >= oldEndU - 0.01 && g.X0 * dx + g.Y0 * dy < newEndU - 0.01);
    }
}
