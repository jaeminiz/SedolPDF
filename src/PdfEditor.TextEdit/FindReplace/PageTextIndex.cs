using System.Diagnostics;
using PdfEditor.Document;
using PdfSharp.Pdf;
using SharpPage = PdfSharp.Pdf.PdfPage;

namespace PdfEditor.TextEdit.FindReplace;

/// <summary>쪽 하나의 글자 위치·논리 글줄. 찾기와 바꾸기 계획이 같은 걷기 결과를 쓴다. PDFium을 부르지 않는다.</summary>
public sealed class PageTextIndex
{
    public SharpPage Page { get; }
    public int PageIndex { get; }
    public PageGlyphWalk Walk { get; }
    public IReadOnlyList<LogicalLine> Lines { get; }

    private PageTextIndex(SharpPage page, int index, PageGlyphWalk walk, IReadOnlyList<LogicalLine> lines)
    {
        Page = page; PageIndex = index; Walk = walk; Lines = lines;
    }

    public static PageTextIndex Build(SharpPage page, int pageIndex)
    {
        var walk = PageGlyphWalker.Walk(page);
        return new PageTextIndex(page, pageIndex, walk, walk.Ok ? LogicalLineBuilder.Build(walk.Glyphs) : []);
    }

    public IReadOnlyList<TextMatch> Find(FindOptions opts, PageId id = default) =>
        [.. TextMatcher.Find(Lines, opts, id, PageIndex).Select(m => m with { Source = this })];
}

/// <summary>쪽 하나를 찾은 결과. 글꼴을 풀 수 없어 못 읽은 글 연산은 조용히 건너뛰지 않고 집계한다(Review Focus 1).</summary>
public sealed record PageFindResult(int PageIndex, PageId Page, IReadOnlyList<TextMatch> Matches, int UnsearchableOps,
    IReadOnlyDictionary<string, int> UnsearchableByFont, string? Failure);

/// <summary>문서 전체(또는 일부 쪽) 찾기 결과.</summary>
public sealed record FindReport(IReadOnlyList<PageFindResult> Pages, TimeSpan Elapsed)
{
    public int TotalMatches => Pages.Sum(p => p.Matches.Count);
    /// <summary>찾을 수 없는 글 연산이 있는 쪽(1부터 센 쪽 번호).</summary>
    public IReadOnlyList<int> PagesWithUnsearchable => [.. Pages.Where(p => p.UnsearchableOps > 0).Select(p => p.PageIndex + 1)];
    public int UnsearchableOps => Pages.Sum(p => p.UnsearchableOps);
    /// <summary>쪽 내용을 해석하지 못한 쪽(1부터 센 쪽 번호).</summary>
    public IReadOnlyList<int> FailedPages => [.. Pages.Where(p => p.Failure is not null).Select(p => p.PageIndex + 1)];
}

/// <summary>쪽 단위로 찾는다. 백그라운드 스레드에서 부르고, 취소하면 OperationCanceledException.</summary>
public static class DocumentTextFinder
{
    /// <param name="pageIndexes">찾을 쪽(0부터). null이면 전체.</param>
    /// <param name="ids">쪽 번호 → PageId(없으면 default).</param>
    /// <param name="progress">끝낸 쪽 수.</param>
    public static FindReport FindAll(PdfDocument doc, FindOptions opts, IReadOnlyList<PageId>? ids = null, IEnumerable<int>? pageIndexes = null,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<PageFindResult>();
        int done = 0;
        foreach (int i in pageIndexes ?? Enumerable.Range(0, doc.PageCount))
        {
            ct.ThrowIfCancellationRequested();
            if (i < 0 || i >= doc.PageCount) continue;
            var idx = PageTextIndex.Build(doc.Pages[i], i);
            var id = ids is not null && i < ids.Count ? ids[i] : default;
            results.Add(new PageFindResult(i, id, idx.Walk.Ok ? idx.Find(opts, id) : [], idx.Walk.UnsearchableOps, idx.Walk.UnsearchableByFont, idx.Walk.Failure));
            progress?.Report(++done);
        }
        return new FindReport(results, sw.Elapsed);
    }
}
