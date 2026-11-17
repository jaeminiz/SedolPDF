using Pdf.Native;
using PdfEditor.ContentStream;
using PdfEditor.ContentStream.Fonts;
using PdfEditor.Document;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit;

/// <summary>한 줄을 원래 글꼴 코드북으로 인코딩한 결과. Width1000 = 새 글의 글꼴 단위(1/1000 글자 크기) 폭 합.</summary>
public sealed record OriginalFontEncoding(byte[] Bytes, bool TwoByte, float Tc, float Tz, float Width1000, float SpaceGap1000 = 0);

/// <summary>
/// 한 줄 편집(원래 글꼴)을 저장 때 내용 스트림에 직접 쓸 수 있도록, 계획 때 원본 글 객체의 글꼴 코드북으로 새 글을 인코딩한다(0.13.0).
/// <see cref="Paragraphs.SessionFileFontSource"/>와 같은 방법(원본 파일을 PDFsharp로 읽어 글 객체 번호 → 글꼴 사전·보인 문자열, V16 규칙).
/// 파일이 없는 새 문서·암호 문서·번호가 어긋난 쪽·ToUnicode 없이 배울 수 없는 글꼴·서브셋에 없는 글자는 null. PDFium 전용 스레드에서 부른다.
/// </summary>
public static class OriginalFontEncoder
{
    public static OriginalFontEncoding? TryEncode(DocumentSession s, PageId page, int originalIndex, string text) => TryEncode(s, page, originalIndex, text, out _);

    /// <summary>못 하면 null과 이유(진단용 짧은 영문 코드).</summary>
    public static OriginalFontEncoding? TryEncode(DocumentSession s, PageId page, int originalIndex, string text, out string? reason)
    {
        reason = null;
        s.Thread.AssertOnThread();
        if (originalIndex < 0 || string.IsNullOrEmpty(text)) { reason = "args"; return null; }
        if (s.IsEncrypted) { reason = "encrypted"; return null; }
        string? source = s.FontSourcePath is { } fsp && File.Exists(fsp) ? fsp : s.FilePath;
        if (source is null || !File.Exists(source)) { reason = "no-file"; return null; }
        int index = s.IndexOf(page);
        if (index < 0) { reason = "no-page"; return null; }
        try
        {
            var kinds = s.GetReplay(page).Originals.Select(o => (ObjectKind)(int)o.Type).ToList();
            string? pdfiumText;
            using (var pg = s.Document.LoadPage(index))
            {
                if (originalIndex >= pg.ObjectCount) { reason = "index"; return null; }
                var obj = pg.GetObject(originalIndex);
                if (obj.Type != PdfPageObjectType.Text) { reason = "not-text"; return null; }
                using var tp = pg.LoadText();
                pdfiumText = obj.GetText(tp);
            }
            if (pdfiumText is null) { reason = "no-text"; return null; }

            using var doc = PdfReader.Open(source, PdfDocumentOpenMode.Import);
            if (index >= doc.PageCount) { reason = "page-mismatch"; return null; }
            var map = PageTextFonts.Build(doc.Pages[index], kinds, out var failure);
            if (map is null) { reason = "object-map:" + failure; return null; }
            if (!map.TryGetValue(originalIndex, out var f)) { reason = "not-in-stream"; return null; }

            var book = FontCodebook.TryBuild(f.Font, f.Strings, [((ReadOnlyMemory<byte>)f.AllBytes, pdfiumText)], out reason);
            if (book is null) { reason = "codebook:" + reason; return null; }
            // 되읽기 확인: 코드북으로 푼 글이 PDFium이 읽은 글과 다르면 쓰지 않는다.
            if (Squash(book.Decode(f.AllBytes)) != Squash(pdfiumText)) { reason = "decode-mismatch"; return null; }
            // 공백 코드가 없는 글꼴(공백을 간격으로 그린 도면): 공백은 글 코드 없이 간격으로 쓴다.
            float gap = 0;
            string toEncode = text;
            if (text.Contains(' ') && !book.Available.Contains(' ')) { gap = DefaultSpaceGap1000; toEncode = text.Replace(" ", ""); if (toEncode.Length == 0) { reason = "only-spaces"; return null; } }
            if (!book.TryEncode(toEncode, out var enc)) { reason = "glyph-not-in-book:" + string.Join(",", toEncode.Where(c => !book.Available.Contains(c)).Distinct().Select(c => "U+" + ((int)c).ToString("X4"))); return null; }
            return new OriginalFontEncoding(enc.Bytes, book.TwoByte, (float)f.Tc, (float)f.Tz, enc.Total1000 + gap * text.Count(c => c == ' '), gap);
        }
        catch (Exception e) when (e is IOException or PdfReaderException or InvalidOperationException or NotSupportedException)
        {
            reason = "exception:" + e.GetType().Name;
            return null;
        }
    }

    private const float DefaultSpaceGap1000 = 250f;

    private static string Squash(string a) => string.Concat(a.Where(c => !char.IsWhiteSpace(c)));
}
