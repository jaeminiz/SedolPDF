using System.Drawing;
using Pdf.Native;
using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.App.Shell;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Saving;
using PdfEditor.Exchange.Summary;
using PdfDocument = Pdf.Native.PdfDocument;

namespace PdfEditor.App.Documents;

/// <summary>요약에 넣을 주석이 있는 쪽 하나(UI 스레드에서 모은 것). 쪽 그림과 번호 표시 위치는 나중에 PDFium 스레드에서 만든다.</summary>
public sealed record SummaryPagePlan(int PageIndex, IReadOnlyList<(CommentItem Root, IReadOnlyList<CommentItem> Replies)> Groups);

public sealed record SummaryOutcome(int AnnotationCount, int SummaryPages, int MissingGlyphs);

/// <summary>
/// 주석 요약 PDF 만들기 흐름 (M3b Task 7). 1) UI 스레드에서 코멘트 목록과 같은 구성으로 쪽별 주석을 모은다(<see cref="Collect"/>).
/// 2) 쪽 그림은 인쇄(PrintJob)와 같이 패치된 사본에서 PDFium으로 그린다 — 편집 중인 PDFium 문서에는 선·텍스트 상자 같은
/// 패치 단계 주석이 없기 때문이다(M3a V10/V11). 암호 문서는 패치를 건너뛰고 세션 문서에서 그린다. PDFium 일은 전용 스레드에
/// 쪽마다 보통 우선순위로 넣어 화면 그리기와 번갈아 처리되고, PDF 쓰기는 스레드 풀에서 한다.
/// </summary>
public static class AnnotationSummaryRunner
{
    public const int ImageLongSidePx = 900;

    /// <summary>주석이 있는 쪽만, 번호는 문서 전체에서 이어서(답글은 번호 없음). UI 스레드. 주석이 없으면 빈 목록.</summary>
    public static IReadOnlyList<SummaryPagePlan> Collect(DocumentTabViewModel d)
    {
        if (d.Session is not { } s) return [];
        using var vm = new CommentsViewModel(d);
        var plans = new List<SummaryPagePlan>();
        var pages = s.Pages;
        for (int i = 0; i < pages.Length; i++)
        {
            var groups = vm.GroupsOfPage(pages[i].Id)
                .Where(g => g.Root.Editable || !IsNotAComment(g.Root))
                .ToList();
            if (groups.Count > 0) plans.Add(new SummaryPagePlan(i, groups));
        }
        return plans;
    }

    // 링크·양식 칸은 코멘트가 아니라서 요약에서 뺀다 (코멘트 목록에는 읽기 전용으로 보인다). 화면 문구가 아니라 PDF 하위 유형으로 가른다.
    private static bool IsNotAComment(CommentItem i) => i.ReadOnlySubtype is PdfAnnotSubtype.Link or PdfAnnotSubtype.Widget;

    public static SummaryText Text() => new(
        Strings.Summary_CreatedFormat, Strings.Summary_CountFormat, Strings.Summary_PageHeadingFormat,
        Strings.Summary_PageHeadingContinuedFormat, Strings.Summary_MissingGlyphsNotice);

    public static async Task<SummaryOutcome> RunAsync(DocumentTabViewModel d, AppearanceServices svc, IReadOnlyList<SummaryPagePlan> plans,
        string outputPath, IProgress<int>? progress, CancellationToken ct)
    {
        if (d.Session is not { } s) throw new InvalidOperationException("No document.");
        var font = svc.Font ?? throw new FileNotFoundException(Strings.Err_SummaryNoFont);
        await s.AnnotationsReady.ConfigureAwait(false);
        var t = s.Thread;

        // 쪽 그림은 인쇄와 같이 사본(주석 패치 + 객체 편집 확정)에서 그린다 (OutputCopyBuilder, M4).
        string? tmp = Path.Combine(Path.GetTempPath(), "~sedol-summary-" + Guid.NewGuid().ToString("N") + ".pdf");
        PdfDocument? patched = null;
        try
        {
            var copy = await new PdfEditor.Document.Output.OutputCopyBuilder(svc.Appearance, svc.Patcher, svc.Assets)
                .BuildIfNeededAsync(s, tmp, ct).ConfigureAwait(false);
            if (copy is not null)
            {
                string path = tmp;
                string? pw = s.Password;
                patched = await t.InvokeAsync(() => PdfDocument.Open(t, path, pw), ct).ConfigureAwait(false);
            }

            // 쪽 그림은 빌더가 쪽을 하나씩 요청할 때 그때 그린다(지연 열거): 한 번에 쪽 그림 하나만 메모리에 있고,
            // 빌더가 압축해 쓴 뒤 놓는다. 이 열거는 스레드 풀(빌더를 돌리는 스레드)에서 돌며, PDFium 일은 전용 스레드에 쪽마다 넣고 기다린다.
            IEnumerable<SummaryPage> Pages()
            {
                int number = 0;
                foreach (var pp in plans)
                {
                    ct.ThrowIfCancellationRequested();
                    var doc = patched;
                    int idx = pp.PageIndex;
                    var rendered = t.InvokeAsync(() => Render(doc ?? s.Document, idx), ct).GetAwaiter().GetResult();
                    var entries = new List<SummaryEntry>(pp.Groups.Count);
                    foreach (var (root, replies) in pp.Groups)
                        entries.Add(ToEntry(root, ++number, replies, rendered.Transform));
                    yield return new SummaryPage(idx + 1, rendered.Bgra, rendered.W, rendered.H, entries);
                }
            }

            var input = new SummaryInput(s.DisplayName, DateTimeOffset.Now, Pages()) { Text = Text() };
            var result = await Task.Run(() => SummaryPdfBuilder.Build(input, font, outputPath, ct, progress), ct).ConfigureAwait(false);
            return new SummaryOutcome(result.EntryCount, result.SummaryPageCount, result.MissingCodepoints.Count);
        }
        finally
        {
            if (patched is not null) { var p = patched; try { await t.InvokeAsync(() => p.Dispose()).ConfigureAwait(false); } catch (Exception) { } }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
        }
    }

    private sealed record Rendered(byte[] Bgra, int W, int H, PageTransform Transform);

    private static Rendered Render(PdfDocument doc, int index)
    {
        using var page = doc.LoadPage(index);
        var size = page.SizePt;   // 회전을 반영한 크기
        double scale = ImageLongSidePx / Math.Max(1.0, Math.Max(size.Width, size.Height));
        var tr = new PageTransform(page.BoundsPt, page.Rotation, scale);
        var vs = tr.ViewSize;
        int w = Math.Max(1, (int)Math.Round(vs.W)), h = Math.Max(1, (int)Math.Round(vs.H));
        return new Rendered(page.RenderTile(scale, 0, 0, w, h, true), w, h, tr);
    }

    private static SummaryEntry ToEntry(CommentItem item, int number, IReadOnlyList<CommentItem> replies, PageTransform tr)
    {
        var r = item.Rect;
        var v = tr.ToView(RectangleF.FromLTRB(r.Left, r.Bottom, r.Right, r.Top));
        var mark = new PdfRectF((float)v.X, (float)v.Y, (float)(v.X + v.W), (float)(v.Y + v.H));
        var rs = replies.Select(x => new SummaryEntry(0, x.KindName, x.Author, x.When, x.Contents, [], mark)).ToList();
        return new SummaryEntry(number, item.KindName, item.Author, item.When, item.Contents, rs, mark);
    }
}
