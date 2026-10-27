using PdfEditor.ContentStream.Fonts;
using PdfSharp.Pdf;

namespace PdfEditor.TextEdit.FindReplace;

/// <summary>
/// 같은 글꼴 사전을 쓰는 문서 전체의 보인 글 코드로 만드는 코드북(0.4.0 확정: 쪽 단위 코드북보다 새 번호의 글자를 훨씬 더 자주 원래 글꼴로 쓸 수 있다).
/// 처음 필요할 때 문서를 한 번 훑고(쪽당 약 1 ms) 이후는 캐시한다. PDFium을 부르지 않는다.
/// </summary>
public sealed class DocumentFontBooks(PdfDocument doc)
{
    private Dictionary<PdfDictionary, List<ReadOnlyMemory<byte>>>? _shown;
    private readonly Dictionary<PdfDictionary, FontCodebook?> _books = new(ReferenceEqualityComparer.Instance);

    public FontCodebook? Get(FontInfo font)
    {
        if (_books.TryGetValue(font.Dict, out var cached)) return cached;
        if (_shown is null)
        {
            _shown = new(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < doc.PageCount; i++)
                foreach (var (dict, strs) in PageGlyphWalker.CollectShownStrings(doc.Pages[i]))
                {
                    if (!_shown.TryGetValue(dict, out var l)) _shown[dict] = l = [];
                    l.AddRange(strs);
                }
        }
        return _books[font.Dict] = _shown.TryGetValue(font.Dict, out var strings) ? FontCodebook.TryBuild(font.Dict, strings) : null;
    }
}
